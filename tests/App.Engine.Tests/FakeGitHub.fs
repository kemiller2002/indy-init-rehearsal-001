/// A fake GitHub REST server with a real object graph (PROVIDER-GITHUB.md §10.1), as an immutable value. Each request
/// returns the next server state and the reply, so a provider program runs against it with no I/O and every request
/// is logged. Fault injection is a list of hooks, each consumed by the first request it matches.
module App.Engine.Tests.FakeGitHub

#nowarn "3261"

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open IndyInitRootCauseInvestigator.Engine
open IndyInitRootCauseInvestigator.Engine.CausaGitHub

type Obj =
    | Blob of byte[]
    | Tree of TreeEntry list
    | Commit of tree: string * parents: string list * message: string

/// What a hook does to the request it matches.
type Action =
    /// Answer with this status, body and headers; the request is not applied.
    | Respond of status: int * body: string * headers: Map<string, string>
    /// Apply the request, then lose the answer (the client sees a timeout).
    | ApplyThenDrop
    /// Lose the request before it is applied (the client sees a timeout).
    | DropUnapplied
    /// Change the server first (another writer, a rewind), then handle the request normally.
    | Before of (Server -> Server)
    /// Handle normally, then replace the answer body with this function of it.
    | AlterReply of (string -> string)

and Hook =
    { Matches: HttpRequest -> bool
      Action: Action }

and Server =
    { Objects: Map<string, Obj>
      Refs: Map<string, string>
      Token: string
      Hooks: Hook list
      Log: HttpRequest list
      /// Durable client writes (the Keep instruction), by key.
      Kept: Map<string, string>
      KeptLog: (string * string) list }

let private sha1Hex (text: string) = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes text))

let private treeId (entries: TreeEntry list) =
    "tree:" + String.Join("|", entries |> List.sortBy (fun e -> e.Name) |> List.map (fun e -> e.Mode + " " + e.Type + " " + e.Name + " " + e.Sha)) |> sha1Hex

let private commitId (tree: string) (parents: string list) (message: string) =
    "commit:" + tree + ":" + String.Join(",", parents) + ":" + message |> sha1Hex

let private putObj (sha: string) (o: Obj) (s: Server) = { s with Objects = s.Objects |> Map.add sha o }

/// Insert path -> blob sha into a tree (nested paths create or replace subtrees); returns the new tree id.
let rec private insert (s: Server) (treeSha: string option) (segments: string list) (blobSha: string) : Server * string =
    let entries =
        match treeSha |> Option.bind (fun t -> Map.tryFind t s.Objects) with
        | Some(Tree es) -> es
        | _ -> []
    match segments with
    | [] -> failwith "empty path"
    | [ name ] ->
        let es = (entries |> List.filter (fun e -> e.Name <> name)) @ [ { Name = name; Mode = "100644"; Type = "blob"; Sha = blobSha } ]
        let id = treeId es
        putObj id (Tree es) s, id
    | name :: rest ->
        let sub = entries |> List.tryFind (fun e -> e.Name = name && e.Type = "tree") |> Option.map (fun e -> e.Sha)
        let s, subId = insert s sub rest blobSha
        let es = (entries |> List.filter (fun e -> e.Name <> name)) @ [ { Name = name; Mode = "040000"; Type = "tree"; Sha = subId } ]
        let id = treeId es
        putObj id (Tree es) s, id

let addBlob (bytes: byte[]) (s: Server) =
    let id = blobId bytes
    putObj id (Blob bytes) s, id

/// Commit `files` (path -> bytes) on top of `branch` (or as a root commit) and move the branch.
let commitFiles (branch: string) (message: string) (files: (string * byte[]) list) (s: Server) =
    let parent = Map.tryFind branch s.Refs
    let baseTree = parent |> Option.bind (fun c -> match Map.tryFind c s.Objects with Some(Commit(t, _, _)) -> Some t | _ -> None)
    let s, tree =
        files
        |> List.fold (fun (s, t) (path, bytes) ->
            let s, b = addBlob bytes s
            let s, nt = insert s t (path.Split('/') |> List.ofArray) b
            s, Some nt) (s, baseTree)
    let tree = tree |> Option.defaultWith (fun () -> treeId [])
    let parents = parent |> Option.toList
    let id = commitId tree parents message
    let s = putObj id (Commit(tree, parents, message)) s
    { s with Refs = s.Refs |> Map.add branch id }, id

let empty (token: string) =
    { Objects = Map.empty; Refs = Map.empty; Token = token; Hooks = []; Log = []; Kept = Map.empty; KeptLog = [] }

let withHook (matches: HttpRequest -> bool) (action: Action) (s: Server) = { s with Hooks = s.Hooks @ [ { Matches = matches; Action = action } ] }

let rec private ancestors (s: Server) (c: string) : Set<string> =
    match Map.tryFind c s.Objects with
    | Some(Commit(_, ps, _)) -> ps |> List.fold (fun acc p -> Set.union acc (ancestors s p)) (Set.singleton c)
    | _ -> Set.singleton c

let private json (o: JsonNode) = o.ToJsonString()
let private obj (pairs: (string * JsonNode) list) =
    let o = JsonObject()
    pairs |> List.iter (fun (k, v) -> o[k] <- v)
    o :> JsonNode
let private jstr (s: string) = JsonValue.Create s :> JsonNode
let private message (m: string) = json (obj [ "message", jstr m ])
let private ok status body = status, body

let private treeJson (sha: string) (entries: TreeEntry list) =
    let arr = JsonArray()
    entries |> List.sortBy (fun e -> e.Name) |> List.iter (fun e -> arr.Add(obj [ "path", jstr e.Name; "mode", jstr e.Mode; "type", jstr e.Type; "sha", jstr e.Sha ]))
    json (obj [ "sha", jstr sha; "tree", arr; "truncated", JsonValue.Create false ])

let private commitJson (sha: string) (tree: string) (parents: string list) =
    let ps = JsonArray()
    parents |> List.iter (fun p -> ps.Add(obj [ "sha", jstr p ]))
    json (obj [ "sha", jstr sha; "tree", obj [ "sha", jstr tree ]; "parents", ps ])

/// Handle one request against the graph (no hooks): new state, status and body.
let private handle (s: Server) (r: HttpRequest) : Server * int * string =
    let auth = r.Headers |> List.tryFind (fun (k, _) -> k = "Authorization") |> Option.map snd
    let path =
        let u = Uri r.Url
        u.AbsolutePath
    let parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray |> List.map Uri.UnescapeDataString
    let body () = JsonDocument.Parse(r.Body |> Option.defaultValue "{}").RootElement
    let writes = r.Method <> "GET"
    if s.Token <> "" && auth <> Some("Bearer " + s.Token) && (writes || auth.IsSome) then s, 401, message "Bad credentials"
    else
        match r.Method, parts with
        | "GET", "repos" :: _ :: _ :: "git" :: "ref" :: "heads" :: branch ->
            match Map.tryFind (String.Join("/", branch)) s.Refs with
            | Some c -> s, 200, json (obj [ "ref", jstr ("refs/heads/" + String.Join("/", branch)); "object", obj [ "type", jstr "commit"; "sha", jstr c ] ])
            | None -> s, 404, message "Not Found"
        | "GET", [ "repos"; _; _; "git"; "commits"; sha ] ->
            match Map.tryFind sha s.Objects with
            | Some(Commit(t, ps, _)) -> s, 200, commitJson sha t ps
            | _ -> s, 404, message "Not Found"
        | "GET", [ "repos"; _; _; "git"; "trees"; sha ] ->
            match Map.tryFind sha s.Objects with
            | Some(Tree es) -> s, 200, treeJson sha es
            | _ -> s, 404, message "Not Found"
        | "GET", [ "repos"; _; _; "git"; "blobs"; sha ] ->
            match Map.tryFind sha s.Objects with
            | Some(Blob b) ->
                let b64 = Convert.ToBase64String b
                let wrapped = String.Join("\n", [ for i in 0..60..(max 0 (b64.Length - 1)) -> b64.Substring(i, min 60 (b64.Length - i)) ]) + "\n"
                s, 200, json (obj [ "sha", jstr sha; "size", JsonValue.Create(int64 b.Length); "encoding", jstr "base64"; "content", jstr wrapped ])
            | _ -> s, 404, message "Not Found"
        | "GET", "repos" :: _ :: _ :: "contents" :: _ -> s, 200, message "stale contents (must never be used)"
        | "POST", [ "repos"; _; _; "git"; "blobs" ] ->
            let b = body ()
            let bytes =
                match b.GetProperty("encoding").GetString() with
                | "base64" -> Convert.FromBase64String(b.GetProperty("content").GetString())
                | _ -> Encoding.UTF8.GetBytes(b.GetProperty("content").GetString())
            let s, id = addBlob bytes s
            s, 201, json (obj [ "sha", jstr id ])
        | "POST", [ "repos"; _; _; "git"; "trees" ] ->
            let b = body ()
            let baseTree = match b.TryGetProperty "base_tree" with | true, v -> Some(v.GetString()) | _ -> None
            let s, tree =
                [ for e in b.GetProperty("tree").EnumerateArray() -> e.GetProperty("path").GetString(), e.GetProperty("sha").GetString() ]
                |> List.fold (fun (s, t) (p, sha) -> let s, nt = insert s t (p.Split('/') |> List.ofArray) sha in s, Some nt) (s, baseTree)
            let id = tree |> Option.get
            let entries = match Map.tryFind id s.Objects with Some(Tree es) -> es | _ -> []
            s, 201, treeJson id entries
        | "POST", [ "repos"; _; _; "git"; "commits" ] ->
            let b = body ()
            let tree = b.GetProperty("tree").GetString()
            let parents = [ for p in b.GetProperty("parents").EnumerateArray() -> p.GetString() ]
            let m = b.GetProperty("message").GetString()
            let id = commitId tree parents m
            putObj id (Commit(tree, parents, m)) s, 201, commitJson id tree parents
        | "PATCH", "repos" :: _ :: _ :: "git" :: "refs" :: "heads" :: branch ->
            let name = String.Join("/", branch)
            let b = body ()
            let target = b.GetProperty("sha").GetString()
            let force = b.GetProperty("force").ValueKind = JsonValueKind.True
            match Map.tryFind name s.Refs with
            | None -> s, 422, message "Reference does not exist"
            | Some current when force || Set.contains current (ancestors s target) ->
                { s with Refs = s.Refs |> Map.add name target }, 200, json (obj [ "ref", jstr ("refs/heads/" + name); "object", obj [ "type", jstr "commit"; "sha", jstr target ] ])
            | Some _ -> s, 422, message "Update is not a fast forward"
        | _ -> s, 404, message "Not Found"

/// Answer one instruction: hooks first (each used once), then the graph.
let answer (s: Server) (i: Instruction) : Server * Reply =
    match i with
    | Keep(key, value) -> { s with Kept = s.Kept |> Map.add key value; KeptLog = s.KeptLog @ [ key, value ] }, Answered(200, "", Map.empty)
    | Call r ->
        let s = { s with Log = s.Log @ [ r ] }
        let hook, rest =
            match s.Hooks |> List.tryFindIndex (fun h -> h.Matches r) with
            | Some ix -> Some s.Hooks[ix], (s.Hooks |> List.removeAt ix)
            | None -> None, s.Hooks
        let s = { s with Hooks = rest }
        match hook |> Option.map (fun h -> h.Action) with
        | Some(Respond(status, body, headers)) -> s, Answered(status, body, headers)
        | Some ApplyThenDrop -> let s, _, _ = handle s r in s, Unanswered "timeout-after-dispatch"
        | Some DropUnapplied -> s, Unanswered "timeout-after-dispatch"
        | Some(Before change) -> let s, status, body = handle (change s) r in s, Answered(status, body, Map.empty)
        | Some(AlterReply f) -> let s, status, body = handle s r in s, Answered(status, f body, Map.empty)
        | None -> let s, status, body = handle s r in s, Answered(status, body, Map.empty)

/// Run a provider program to completion against the fake.
let rec run (s: Server) (m: Remote<'a>) : 'a * Server =
    match m with
    | Done a -> a, s
    | Next(i, k) ->
        let s, reply = answer s i
        run s (k reply)

/// Read a file at the branch head (test inspection only).
let fileAt (s: Server) (branch: string) (path: string) : byte[] option =
    let rec go (tree: string) (segments: string list) =
        match Map.tryFind tree s.Objects, segments with
        | Some(Tree es), [ name ] -> es |> List.tryFind (fun e -> e.Name = name) |> Option.bind (fun e -> match Map.tryFind e.Sha s.Objects with Some(Blob b) -> Some b | _ -> None)
        | Some(Tree es), name :: rest -> es |> List.tryFind (fun e -> e.Name = name && e.Type = "tree") |> Option.bind (fun e -> go e.Sha rest)
        | _ -> None
    Map.tryFind branch s.Refs
    |> Option.bind (fun c -> match Map.tryFind c s.Objects with Some(Commit(t, _, _)) -> Some t | _ -> None)
    |> Option.bind (fun t -> go t (path.Split('/') |> List.ofArray))

/// The tree id of a directory at a commit (test inspection only).
let dirAt (s: Server) (commit: string) (path: string) : string option =
    let rec go (tree: string) (segments: string list) =
        match segments with
        | [] -> Some tree
        | name :: rest ->
            match Map.tryFind tree s.Objects with
            | Some(Tree es) -> es |> List.tryFind (fun e -> e.Name = name && e.Type = "tree") |> Option.bind (fun e -> go e.Sha rest)
            | _ -> None
    match Map.tryFind commit s.Objects with
    | Some(Commit(t, _, _)) -> go t (path.Split('/', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray)
    | _ -> None

let parentsOf (s: Server) (commit: string) = match Map.tryFind commit s.Objects with Some(Commit(_, ps, _)) -> ps | _ -> []
let commitCount (s: Server) = s.Objects |> Map.filter (fun _ o -> match o with Commit _ -> true | _ -> false) |> Map.count
