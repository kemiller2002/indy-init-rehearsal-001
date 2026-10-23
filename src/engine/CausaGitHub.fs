/// The Causa storage v1.0.0 GitHub provider: one atomic Git Data API commit per publish, guarded, fast-forward only,
/// confirmed by a re-read addressed by commit SHA (Indy-init requirements/causa/data/v1.0.0/PROVIDER-GITHUB.md,
/// CAUSA-PROV-01…36; CONTRACT §9.4).
///
/// Every operation is a pure *program*: a value that is either finished or names the next request (or durable local
/// write) together with what to do with its answer. The shell runs one step at a time through Limen effects; the tests
/// run the very same programs against an in-memory GitHub. Nothing here performs I/O, reads a clock or draws entropy.
module IndyInitRootCauseInvestigator.Engine.CausaGitHub

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Causa.Contract

// ---------------------------------------------------------------------------------------------------------------------
// Configuration and paths (CAUSA-PROV-01…03)
// ---------------------------------------------------------------------------------------------------------------------

/// Where demonstration writes go. Contains no credential (CAUSA-PROV-01 §4).
type Workspace =
    { Owner: string
      Repo: string
      Branch: string
      RunId: string }

[<RequireQualifiedAccess>]
module Workspace =
    // \z, not $: CONTRACT §2.13 evaluates patterns with ECMA-262 semantics, where $ never matches before a final LF.
    let private ownerPattern = Regex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})\z", RegexOptions.CultureInvariant)
    let private repoPattern = Regex(@"^[A-Za-z0-9._-]{1,100}\z", RegexOptions.CultureInvariant)
    let private branchPattern = Regex(@"^[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*\z", RegexOptions.CultureInvariant)
    let private runIdPattern = Regex(@"^[a-z0-9][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant)
    let private uuidPattern = Regex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.CultureInvariant)

    let ownerValid (s: string) = ownerPattern.IsMatch s
    let repoValid (s: string) = repoPattern.IsMatch s && s <> "." && s <> ".."
    let branchValid (s: string) =
        branchPattern.IsMatch s && not (s.EndsWith(".lock", StringComparison.Ordinal)) && not (s.Contains("..", StringComparison.Ordinal))
        && s.Split('/') |> Array.forall (fun seg -> not (seg.StartsWith(".", StringComparison.Ordinal)) && not (seg.EndsWith(".lock", StringComparison.Ordinal)))
    let runIdValid (s: string) = runIdPattern.IsMatch s
    let investigationIdValid (s: string) = uuidPattern.IsMatch s

    /// Owner and repository compare case-insensitively (GitHub semantics); branch and runId exactly.
    let identity (w: Workspace) = w.Owner.ToLowerInvariant() + "/" + w.Repo.ToLowerInvariant() + "@" + w.Branch + "#" + w.RunId

    /// CAUSA-PROV-02 §2: `<label>-<yyyymmdd>-<hhmmss>-<hex6>`, UTC, hex6 from the boundary's random source.
    let newRunId (label: string) (now: DateTimeOffset) (entropy: Guid) =
        let hex6 = Convert.ToHexStringLower(entropy.ToByteArray()).Substring(0, 6)
        let clean = String(label.ToLowerInvariant() |> Seq.filter (fun c -> Char.IsAsciiLetterLower c || Char.IsAsciiDigit c || c = '-') |> Seq.toArray)
        let stamp = now.UtcDateTime.ToString("yyyyMMdd-HHmmss", Globalization.CultureInfo.InvariantCulture)
        let tail = "-" + stamp + "-" + hex6
        let label = if clean = "" || not (Char.IsAsciiLetterLower clean[0] || Char.IsAsciiDigit clean[0]) then "demo" else clean
        label.Substring(0, min label.Length (64 - tail.Length)) + tail

    let segments (w: Workspace) (investigationId: string) = [ "workspaces"; "demo"; w.RunId; "investigations"; investigationId ]

    /// P = workspaces/demo/<runId>/investigations/<investigationId>/ (CAUSA-PROV-03 §1).
    let prefix (w: Workspace) (investigationId: string) = String.Join("/", segments w investigationId) + "/"

    /// CAUSA-PROV-03 §2: checked before any request. Frozen trees are never a target.
    let checkTarget (w: Workspace) (investigationId: string) : Result<string, CausaError> =
        if not (runIdValid w.RunId) then CausaError.fail Codes.pathInvalid ("the run id '" + w.RunId + "' is not a valid runId")
        elif not (investigationIdValid investigationId) then CausaError.fail Codes.pathInvalid ("'" + investigationId + "' is not a canonical investigation id")
        else
            let p = prefix w investigationId
            let frozen = [ "fixtures/v1.0.0/"; "contract/v1.0.0/"; "compatibility/v1.0.0/" ]
            if not (p.StartsWith("workspaces/demo/" + w.RunId + "/investigations/", StringComparison.Ordinal)) || frozen |> List.exists (fun f -> p.StartsWith(f, StringComparison.Ordinal)) then
                CausaError.fail "CAUSA.PATH_FROZEN" (p + " is outside the writable workspace")
            else Ok p

    let check (w: Workspace) : Result<Workspace, string> =
        if not (ownerValid w.Owner) then Error "The owner is not a valid GitHub account name."
        elif not (repoValid w.Repo) then Error "The repository name is not valid."
        elif not (branchValid w.Branch) then Error "The branch name is not valid."
        elif not (runIdValid w.RunId) then Error "The run id is not valid (CAUSA.PATH_INVALID)."
        else Ok w

// ---------------------------------------------------------------------------------------------------------------------
// Git object identity (CAUSA-PROV-14)
// ---------------------------------------------------------------------------------------------------------------------

/// Lowercase hex SHA-1 of "blob <length>\0<bytes>", as Git computes it.
let blobId (bytes: byte[]) =
    let header = Encoding.ASCII.GetBytes("blob " + string bytes.Length + "\000")
    Convert.ToHexStringLower(SHA1.HashData(Array.append header bytes))

let private fileBytes (files: InvestigationFiles) =
    [ "manifest.json", files.Manifest; "events.ndjson", files.Events; "snapshot.json", files.Snapshot ]

// ---------------------------------------------------------------------------------------------------------------------
// Programs
// ---------------------------------------------------------------------------------------------------------------------

/// How a step was answered. `Unanswered` means the request may have been applied (timeout or connection lost after
/// dispatch); `NotSent` means it certainly was not (offline, a safe method that failed, a refused local write).
type Reply =
    | Answered of status: int * body: string * headers: Map<string, string>
    | NotSent of reason: string
    | Unanswered of reason: string

type Instruction =
    | Call of HttpRequest
    /// A durable local write that must complete before the program continues (CAUSA-PROV-13 step 7).
    | Keep of key: string * value: string

type Remote<'a> =
    | Done of 'a
    | Next of Instruction * (Reply -> Remote<'a>)

[<RequireQualifiedAccess>]
module Remote =
    let rec bind (f: 'a -> Remote<'b>) (m: Remote<'a>) : Remote<'b> =
        match m with
        | Done a -> f a
        | Next(i, k) -> Next(i, k >> bind f)

    let map (f: 'a -> 'b) = bind (f >> Done)

    /// Requests a program makes, in order, given a fixed answer function (used by tests and evidence).
    let rec run (answer: Instruction -> Reply) (m: Remote<'a>) : 'a =
        match m with
        | Done a -> a
        | Next(i, k) -> run answer (k (answer i))

// ---------------------------------------------------------------------------------------------------------------------
// Faults and the status-code mapping (CAUSA-PROV-19)
// ---------------------------------------------------------------------------------------------------------------------

type FaultKind =
    | Authentication
    | Authorization
    | RateLimited of waitSeconds: int64 option
    | ServerError
    | NoResponse
    | NotDelivered
    | NotFound
    | NonFastForward
    | ObjectMissing
    | Rejected
    | Redirected
    | Unreadable
    | ByteMismatch
    | PathFrozen
    | PathInvalid
    | Truncated

type Fault =
    { Kind: FaultKind
      /// R (reads), O (object creation and guard), U (the PATCH), C (confirmation and resolution).
      Phase: string
      Status: int option
      Cause: string
      Detail: string }

[<RequireQualifiedAccess>]
module Fault =
    /// The structured provider fault for people (CAUSA-PROV-19 §3; PERSISTENCE-CONTRACT §42).
    let category (f: Fault) =
        match f.Kind with
        | Authentication -> "authentication"
        | Authorization -> "authorization"
        | RateLimited _ -> "rate-limited"
        | ServerError | NoResponse | NotDelivered -> "unavailable"
        | NonFastForward -> "conflict"
        | NotFound | Redirected | Rejected | ObjectMissing -> "configuration"
        | Unreadable | ByteMismatch | PathFrozen | PathInvalid | Truncated -> "integrity"

    let describe (f: Fault) =
        category f + ": " + f.Detail + (match f.Status with Some s -> " (HTTP " + string s + ")" | None -> "")

let private headerOf (name: string) (headers: Map<string, string>) = Map.tryFind name headers

let private messageOf (body: string) =
    try
        use doc = JsonDocument.Parse body
        match doc.RootElement.ValueKind, doc.RootElement.TryGetProperty "message" with
        | JsonValueKind.Object, (true, m) when m.ValueKind = JsonValueKind.String -> m.GetString() |> Option.ofObj |> Option.defaultValue ""
        | _ -> ""
    with _ -> ""

/// Classify a response that is not the expected success. Pure: the same status, body and headers always give the
/// same kind (CAUSA-PROV-19). Rate limits are recognised before any other 403.
let classify (phase: string) (status: int) (body: string) (headers: Map<string, string>) : Fault =
    let message = messageOf body
    let lower = message.ToLowerInvariant()
    let fault kind cause = { Kind = kind; Phase = phase; Status = Some status; Cause = cause; Detail = (if message = "" then "GitHub answered " + string status else message) }
    let wait =
        match headerOf "retry-after" headers |> Option.bind (fun v -> match Int64.TryParse v with | true, n -> Some n | _ -> None) with
        | Some s -> Some s
        | None -> None
    let rateLimited =
        headerOf "x-ratelimit-remaining" headers = Some "0" || (headerOf "retry-after" headers).IsSome || lower.Contains("rate limit")
    match status with
    | 401 -> fault Authentication "http-401"
    | 403 when rateLimited -> fault (RateLimited wait) "http-403-rate-limit"
    | 403 -> fault Authorization "http-403"
    | 404 -> fault NotFound "http-404"
    | 409 | 422 when lower.Contains("not a fast forward") -> fault NonFastForward ("http-" + string status + "-non-ff")
    | 422 when lower.Contains("object does not exist") -> fault ObjectMissing "http-422-object-missing"
    | 409 | 422 -> fault Rejected ("http-" + string status)
    | 429 -> fault (RateLimited wait) "http-429"
    | 301 | 302 | 307 | 308 -> fault Redirected ("http-" + string status)
    | s when s >= 500 -> fault ServerError ("http-" + string s)
    | _ -> fault Rejected ("http-" + string status)

let private noAnswer (phase: string) (reply: Reply) =
    match reply with
    | NotSent r -> { Kind = NotDelivered; Phase = phase; Status = None; Cause = "not-sent"; Detail = "the request was not sent (" + r + ")" }
    | Unanswered r -> { Kind = NoResponse; Phase = phase; Status = None; Cause = "no-response"; Detail = "no answer from GitHub (" + r + ")" }
    | Answered(s, _, _) -> { Kind = Unreadable; Phase = phase; Status = Some s; Cause = "unexpected"; Detail = "unexpected answer" }

let private integrity (kind: FaultKind) (phase: string) (cause: string) (detail: string) =
    { Kind = kind; Phase = phase; Status = None; Cause = cause; Detail = detail }

// ---------------------------------------------------------------------------------------------------------------------
// Requests (CAUSA-PROV-05, -13, -31)
// ---------------------------------------------------------------------------------------------------------------------

/// What every request needs: the workspace, the in-memory token ("" for anonymous) and a nonce for uncached ref reads.
type Session =
    { Workspace: Workspace
      Token: string
      ApiBase: string
      Nonce: string }

let apiBase = "https://api.github.com"

let private repoUrl (s: Session) =
    s.ApiBase + "/repos/" + Uri.EscapeDataString s.Workspace.Owner + "/" + Uri.EscapeDataString s.Workspace.Repo

let private branchPath (s: Session) = s.Workspace.Branch.Split('/') |> Array.map Uri.EscapeDataString |> String.concat "/"

/// CAUSA-PROV-31 §1: the token travels only in the Authorization header, and only to the API origin.
let private headers (s: Session) (withBody: bool) =
    (if s.Token = "" then [] else [ "Authorization", "Bearer " + s.Token ])
    @ [ "Accept", "application/vnd.github+json"; "X-GitHub-Api-Version", "2022-11-28" ]
    @ (if withBody then [ "Content-Type", "application/json" ] else [])

let private get (s: Session) (url: string) = Call { Method = "GET"; Url = url; Headers = headers s false; Body = None }

let private send (s: Session) (method: string) (url: string) (body: JsonNode) =
    Call { Method = method; Url = url; Headers = headers s true; Body = Some(body.ToJsonString()) }

/// Ask, then read a JSON success with `read`; anything else becomes a fault of `phase`.
let private ask (phase: string) (ok: int list) (instruction: Instruction) (read: JsonElement -> Result<'a, string>) : Remote<Result<'a, Fault>> =
    Next(
        instruction,
        fun reply ->
            match reply with
            | Answered(status, body, _) when List.contains status ok ->
                let parsed =
                    try
                        use doc = JsonDocument.Parse body
                        read doc.RootElement
                    with ex -> Error ex.Message
                Done(parsed |> Result.mapError (fun e -> { Kind = Unreadable; Phase = phase; Status = Some status; Cause = "unreadable-body"; Detail = "GitHub's answer could not be read: " + e }))
            | Answered(status, body, h) -> Done(Error(classify phase status body h))
            | other -> Done(Error(noAnswer phase other))
    )

let private str (name: string) (e: JsonElement) =
    match e.ValueKind, (if e.ValueKind = JsonValueKind.Object then e.TryGetProperty name else (false, Unchecked.defaultof<JsonElement>)) with
    | JsonValueKind.Object, (true, v) when v.ValueKind = JsonValueKind.String -> Ok(v.GetString() |> Option.ofObj |> Option.defaultValue "")
    | _ -> Error("missing " + name)

let private child (name: string) (e: JsonElement) =
    match e.ValueKind, (if e.ValueKind = JsonValueKind.Object then e.TryGetProperty name else (false, Unchecked.defaultof<JsonElement>)) with
    | JsonValueKind.Object, (true, v) -> Ok v
    | _ -> Error("missing " + name)

type GitBuilder() =
    member _.Bind(m: Remote<Result<'a, 'e>>, f: 'a -> Remote<Result<'b, 'e>>) : Remote<Result<'b, 'e>> =
        m |> Remote.bind (function Ok a -> f a | Error e -> Done(Error e))
    member _.Bind(r: Result<'a, 'e>, f: 'a -> Remote<Result<'b, 'e>>) : Remote<Result<'b, 'e>> =
        match r with Ok a -> f a | Error e -> Done(Error e)
    member _.Return(a: 'a) : Remote<Result<'a, 'e>> = Done(Ok a)
    member _.ReturnFrom(m: Remote<Result<'a, 'e>>) = m
    member _.Zero() : Remote<Result<unit, 'e>> = Done(Ok())
    member _.Delay(f: unit -> Remote<Result<'a, 'e>>) = f ()

let git = GitBuilder()

type TreeEntry =
    { Name: string
      Mode: string
      Type: string
      Sha: string }

type CommitInfo = { Tree: string; Parents: string list }

[<RequireQualifiedAccess>]
module Git =
    /// CAUSA-PROV-05 step 1 / CAUSA-PROV-06 §2: the one branch-addressed read, never served from a cache. The kernel
    /// cannot set fetch's `cache` option, so a per-request nonce makes every ref read a distinct URL.
    let head (phase: string) (s: Session) (round: int) : Remote<Result<string, Fault>> =
        ask phase [ 200 ] (get s (repoUrl s + "/git/ref/heads/" + branchPath s + "?nocache=" + Uri.EscapeDataString(s.Nonce + "-" + string round))) (fun e ->
            child "object" e
            |> Result.bind (fun o ->
                match str "type" o, str "sha" o with
                | Ok "commit", Ok sha -> Ok sha
                | Ok other, _ -> Error("the branch names a " + other + ", not a commit")
                | Error m, _ | _, Error m -> Error m))

    let commit (phase: string) (s: Session) (sha: string) : Remote<Result<CommitInfo, Fault>> =
        ask phase [ 200 ] (get s (repoUrl s + "/git/commits/" + sha)) (fun e ->
            match child "tree" e |> Result.bind (str "sha"), child "parents" e with
            | Ok tree, Ok ps when ps.ValueKind = JsonValueKind.Array ->
                Ok { Tree = tree; Parents = [ for p in ps.EnumerateArray() -> match str "sha" p with Ok s -> s | Error _ -> "" ] }
            | Error m, _ -> Error m
            | _ -> Error "missing parents")

    /// Non-recursive tree read; a truncated listing is never treated as complete (CAUSA-PROV-05 §3).
    let tree (phase: string) (s: Session) (sha: string) : Remote<Result<TreeEntry list, Fault>> =
        ask phase [ 200 ] (get s (repoUrl s + "/git/trees/" + sha)) (fun e ->
            match e.TryGetProperty "truncated" with
            | true, t when t.ValueKind = JsonValueKind.True -> Error "truncated"
            | _ ->
                child "tree" e
                |> Result.map (fun xs ->
                    [ for x in xs.EnumerateArray() ->
                          { Name = str "path" x |> Result.defaultValue ""
                            Mode = str "mode" x |> Result.defaultValue ""
                            Type = str "type" x |> Result.defaultValue ""
                            Sha = str "sha" x |> Result.defaultValue "" } ]))
        |> Remote.map (Result.mapError (fun f -> if f.Detail.EndsWith("truncated", StringComparison.Ordinal) then { f with Kind = Truncated; Cause = "truncated" } else f))

    /// CAUSA-PROV-05 step 5: base64 only, the size must match and the bytes must hash to the requested blob id.
    let blob (phase: string) (s: Session) (sha: string) : Remote<Result<byte[], Fault>> =
        ask phase [ 200 ] (get s (repoUrl s + "/git/blobs/" + sha)) (fun e ->
            match str "encoding" e, str "content" e, (match e.TryGetProperty "size" with | true, v when v.ValueKind = JsonValueKind.Number -> Ok(v.GetInt64()) | _ -> Error "missing size") with
            | Ok "base64", Ok content, Ok size ->
                let bytes = Convert.FromBase64String(content.Replace("\n", "").Replace("\r", ""))
                if int64 bytes.Length <> size then Error "the blob size does not match its bytes"
                elif blobId bytes <> sha then Error "the blob bytes do not hash to the requested id"
                else Ok bytes
            | Ok other, _, _ -> Error("unsupported blob encoding " + other)
            | Error m, _, _ | _, Error m, _ | _, _, Error m -> Error m)

    /// CAUSA-PROV-14: base64 of the exact bytes; the returned id must equal the locally computed git blob id.
    let createBlob (s: Session) (bytes: byte[]) : Remote<Result<string, Fault>> =
        let body = JsonObject()
        body["content"] <- JsonValue.Create(Convert.ToBase64String bytes)
        body["encoding"] <- JsonValue.Create "base64"
        let expected = blobId bytes
        ask "O" [ 201 ] (send s "POST" (repoUrl s + "/git/blobs") body) (str "sha")
        |> Remote.map (
            Result.bind (fun sha ->
                if sha = expected then Ok sha
                else Error(integrity ByteMismatch "O" "byte-mismatch" ("GitHub stored blob " + sha + " for bytes whose git blob id is " + expected)))
        )

    let createTree (s: Session) (baseTree: string) (entries: (string * string) list) : Remote<Result<string, Fault>> =
        let body = JsonObject()
        body["base_tree"] <- JsonValue.Create baseTree
        let items = JsonArray()
        entries
        |> List.iter (fun (path, sha) ->
            let o = JsonObject()
            o["path"] <- JsonValue.Create path
            o["mode"] <- JsonValue.Create "100644"
            o["type"] <- JsonValue.Create "blob"
            o["sha"] <- JsonValue.Create sha
            items.Add o)
        body["tree"] <- items
        ask "O" [ 201 ] (send s "POST" (repoUrl s + "/git/trees") body) (str "sha")

    /// CAUSA-PROV-18 §3: a non-personal author; the GitHub user never becomes contract data.
    let createCommit (s: Session) (message: string) (tree: string) (parent: string) : Remote<Result<string, Fault>> =
        let body = JsonObject()
        body["message"] <- JsonValue.Create message
        body["tree"] <- JsonValue.Create tree
        let parents = JsonArray()
        parents.Add(JsonValue.Create parent)
        body["parents"] <- parents
        let author = JsonObject()
        author["name"] <- JsonValue.Create "causa-adapter"
        author["email"] <- JsonValue.Create "causa-adapter@example.invalid"
        body["author"] <- author
        ask "O" [ 201 ] (send s "POST" (repoUrl s + "/git/commits") body) (fun e ->
            match str "sha" e, child "tree" e |> Result.bind (str "sha"), child "parents" e with
            | Ok sha, Ok t, Ok ps when t = tree && [ for p in ps.EnumerateArray() -> str "sha" p |> Result.defaultValue "" ] = [ parent ] -> Ok sha
            | Ok _, Ok _, Ok _ -> Error "the created commit does not have the requested tree and single parent"
            | Error m, _, _ | _, Error m, _ | _, _, Error m -> Error m)

    /// CAUSA-PROV-13 step 9: `force` is the JSON literal false, always.
    let patchRequest (s: Session) (commitSha: string) =
        let body = JsonObject()
        body["sha"] <- JsonValue.Create commitSha
        body["force"] <- JsonValue.Create false
        send s "PATCH" (repoUrl s + "/git/refs/heads/" + branchPath s) body

// ---------------------------------------------------------------------------------------------------------------------
// Reading one investigation at one commit (CAUSA-PROV-05, -07)
// ---------------------------------------------------------------------------------------------------------------------

type Dir =
    { DirSha: string
      Entries: TreeEntry list }

type AtCommit =
    { Commit: string
      Tree: string
      Dir: Dir option }

/// Walk from a root tree along the segments by exact name; absent at any level means absent.
let rec private walk (phase: string) (s: Session) (treeSha: string) (segments: string list) : Remote<Result<Dir option, Fault>> =
    git {
        let! entries = Git.tree phase s treeSha
        match segments with
        | [] -> return Some { DirSha = treeSha; Entries = entries }
        | next :: rest ->
            match entries |> List.tryFind (fun e -> e.Name = next) with
            | None -> return None
            | Some e when e.Type = "tree" && e.Mode = "040000" -> return! walk phase s e.Sha rest
            | Some _ -> return! Done(Error(integrity PathInvalid phase Codes.pathInvalid (next + " is not a directory")))
    }

let private investigationDir (phase: string) (s: Session) (rootTree: string) (iid: string) =
    walk phase s rootTree (Workspace.segments s.Workspace iid)

/// CONTRACT §3.3: exactly the three files, as regular blobs.
let private checkEntries (phase: string) (dir: Dir) : Result<Map<string, string>, Fault> =
    let names = dir.Entries |> List.map (fun e -> e.Name) |> List.sort
    let regular = dir.Entries |> List.forall (fun e -> e.Type = "blob" && (e.Mode = "100644" || e.Mode = "100755"))
    if names <> List.sort Store.fileNames || not regular then
        Error(integrity PathInvalid phase Codes.pathInvalid ("the investigation directory holds " + String.Join(", ", names) + " instead of exactly manifest.json, events.ndjson and snapshot.json"))
    else Ok(dir.Entries |> List.map (fun e -> e.Name, e.Sha) |> Map.ofList)

let private readFiles (phase: string) (s: Session) (iid: string) (dir: Dir) : Remote<Result<InvestigationFiles, Fault>> =
    git {
        let! shas = checkEntries phase dir
        let! manifest = Git.blob phase s shas["manifest.json"]
        let! events = Git.blob phase s shas["events.ndjson"]
        let! snapshot = Git.blob phase s shas["snapshot.json"]
        return { DirectoryId = iid; Manifest = manifest; Events = events; Snapshot = snapshot }
    }

let private at (phase: string) (s: Session) (iid: string) (commitSha: string) : Remote<Result<AtCommit, Fault>> =
    git {
        let! c = Git.commit phase s commitSha
        let! dir = investigationDir phase s c.Tree iid
        return { Commit = commitSha; Tree = c.Tree; Dir = dir }
    }

/// The three files' blob ids, as the tree entries of an investigation directory would name them.
let private shasOf (files: InvestigationFiles) = fileBytes files |> List.map (fun (n, b) -> n, blobId b) |> Map.ofList

let private sameAs (dir: Dir option) (files: InvestigationFiles option) =
    match dir, files with
    | None, None -> true
    | Some d, Some f -> (checkEntries "R" d |> Result.toOption) = Some(shasOf f)
    | _ -> false

// ---------------------------------------------------------------------------------------------------------------------
// Path-prefix guard (CAUSA-PROV-15)
// ---------------------------------------------------------------------------------------------------------------------

let rec private guardLevel (s: Session) (baseTree: string option) (newTree: string) (segments: string list) (expected: Map<string, string>) : Remote<Result<unit, Fault>> =
    let frozen detail = Done(Error(integrity PathFrozen "O" "CAUSA.PATH_FROZEN" detail))
    git {
        let! baseEntries = match baseTree with Some t -> Git.tree "O" s t | None -> Done(Ok [])
        let! newEntries = Git.tree "O" s newTree
        match segments with
        | [] ->
            let got = newEntries |> List.map (fun e -> e.Name, (e.Mode, e.Type, e.Sha)) |> Map.ofList
            let want = expected |> Map.map (fun _ sha -> ("100644", "blob", sha))
            if got = want then return () else return! frozen "the new investigation directory is not exactly the three written files"
        | next :: rest ->
            let others (xs: TreeEntry list) = xs |> List.filter (fun e -> e.Name <> next) |> List.sortBy (fun e -> e.Name)
            if others baseEntries <> others newEntries then
                return! frozen ("the new tree changes entries beside '" + next + "'; only the investigation directory may differ")
            else
                match newEntries |> List.tryFind (fun e -> e.Name = next) with
                | Some n when n.Type = "tree" && n.Mode = "040000" ->
                    let baseNext = baseEntries |> List.tryFind (fun e -> e.Name = next && e.Type = "tree") |> Option.map (fun e -> e.Sha)
                    return! guardLevel s baseNext n.Sha rest expected
                | _ -> return! frozen ("'" + next + "' is not a directory in the new tree")
    }

/// Prove that `newTree` differs from `baseTree` only under P, by walking both along P (never a truncated listing).
let guard (s: Session) (iid: string) (baseTree: string) (newTree: string) (expected: Map<string, string>) =
    guardLevel s (Some baseTree) newTree (Workspace.segments s.Workspace iid) expected

// ---------------------------------------------------------------------------------------------------------------------
// Sync states (CAUSA-PROV-21) and the confirmed copy
// ---------------------------------------------------------------------------------------------------------------------

type SyncState =
    | SyncPending
    | SyncSending
    | SyncUnknown
    | SyncConfirmed
    | SyncConflicted
    | SyncRefused
    | SyncFailed

[<RequireQualifiedAccess>]
module SyncState =
    let wire (s: SyncState) =
        match s with
        | SyncPending -> "pending"
        | SyncSending -> "sending"
        | SyncUnknown -> "unknown"
        | SyncConfirmed -> "confirmed"
        | SyncConflicted -> "conflicted"
        | SyncRefused -> "refused"
        | SyncFailed -> "failed"

    let ofWire (s: string) =
        match s with
        | "pending" -> Some SyncPending
        | "sending" -> Some SyncSending
        | "unknown" -> Some SyncUnknown
        | "confirmed" -> Some SyncConfirmed
        | "conflicted" -> Some SyncConflicted
        | "refused" -> Some SyncRefused
        | "failed" -> Some SyncFailed
        | _ -> None

    /// CAUSA-PROV-23: only `confirmed` is success; no state borrows another's words.
    let label (s: SyncState) =
        match s with
        | SyncPending -> "Not yet on GitHub — waiting to be sent"
        | SyncSending -> "Sending to GitHub…"
        | SyncUnknown -> "Outcome unknown — checking whether GitHub received it"
        | SyncConfirmed -> "Saved to GitHub (confirmed by re-reading the commit)"
        | SyncConflicted -> "Conflict — GitHub has a different version"
        | SyncRefused -> "Refused — GitHub already has a different investigation here"
        | SyncFailed -> "Failed — GitHub did not accept the change"

/// The remote copy as last confirmed: the three files and the commit and tree at which they were read or written.
type Confirmed =
    { Commit: string
      Tree: string
      Files: InvestigationFiles }

/// CAUSA-PROV-13 step 7: recorded durably before the PATCH is sent.
type Marker =
    { Commit: string
      Tree: string
      Parent: string
      PatchSentAt: string }

type Outcome =
    /// T6/T9: re-read by commit SHA returned exactly the written bytes.
    | Confirmed of Confirmed
    /// T3/T10: definitively not applied; may be retried.
    | BackToPending of Fault
    /// T4: definitively not applied; needs the user or operator.
    | Failed of Fault
    /// T5/T12: the PATCH may have landed; resolve before anything else is sent.
    | Unknown of cause: string * detail: string
    /// T8/T11: GitHub's copy changed under this one. `remote` is its verified state when readable.
    | Conflicted of cause: string * detail: string * remote: Confirmed option
    /// T15: a create whose directory already exists.
    | Refused of cause: string * detail: string

/// One publish: the files to write over the confirmed copy (None for a new directory).
type Attempt =
    { Session: Session
      InvestigationId: string
      Base: Confirmed option
      Files: InvestigationFiles
      Message: string
      Now: string
      /// Verify a remote file set with all 17 reader stages (CAUSA-PROV-07 §1).
      Verify: InvestigationFiles -> Result<unit, CausaError>
      /// The durable step-7 write for a marker: (storage key, value). Never contains the token.
      Record: Marker -> string * string }

let maxRounds = 5

let private startsWith (whole: byte[]) (prefix: byte[]) = whole.AsSpan().StartsWith(prefix.AsSpan())

/// CAUSA-PROV-27 §4 / CAUSA-PROV-07 §2: the investigation changed at `head`; classify without writing anything.
let private changedAt (a: Attempt) (head: AtCommit) : Remote<Outcome> =
    let s = a.Session
    match head.Dir with
    | None ->
        match a.Base with
        | Some _ -> Done(Conflicted(Codes.historyRewritten, "the investigation is no longer on the branch", None))
        | None -> Done(BackToPending(integrity NonFastForward "R" "head-moved" "the branch moved"))
    | Some dir ->
        readFiles "R" s a.InvestigationId dir
        |> Remote.map (fun read ->
            match read with
            | Error f when f.Kind = PathInvalid -> Conflicted(Codes.pathInvalid, f.Detail, None)
            | Error f when a.Base.IsNone -> Refused(Codes.versionStale, "the investigation directory already exists on the branch (" + f.Detail + ")")
            | Error f -> BackToPending f
            | Ok remote ->
                let remoteCopy = { Commit = head.Commit; Tree = head.Tree; Files = remote }
                if remote.Events = a.Files.Events && remote.Manifest = a.Files.Manifest && remote.Snapshot = a.Files.Snapshot then
                    // The same bytes are already there: a write of ours landed whose confirmation was lost.
                    Confirmed remoteCopy
                else
                    match a.Base, a.Verify remote with
                    | None, _ -> Refused(Codes.versionStale, "the investigation directory already exists on the branch; nothing was written")
                    | Some _, Error e -> Conflicted(e.Code, "GitHub's copy fails verification: " + e.Detail, None)
                    | Some b, Ok() when not (startsWith remote.Events b.Files.Events) ->
                        Conflicted(Codes.historyRewritten, "GitHub's events.ndjson no longer starts with the confirmed history", Some remoteCopy)
                    | Some _, Ok() -> Conflicted(Codes.versionStale, "someone else appended to this investigation on GitHub", Some remoteCopy))

/// CAUSA-PROV-17: read commit C by SHA and compare every written byte.
let private confirm (a: Attempt) (c: string) (nt: string) (tc: string) : Remote<Outcome> =
    let s = a.Session
    git {
        let! info = Git.commit "C" s c
        if info.Tree <> nt || info.Parents <> [ tc ] then
            return! Done(Error(integrity Unreadable "C" "CAUSA.WRITE_UNCONFIRMED" "the commit read back does not have the written tree and parent"))
        else
            let! dir = investigationDir "C" s nt a.InvestigationId
            match dir with
            | None -> return! Done(Error(integrity Unreadable "C" "CAUSA.WRITE_UNCONFIRMED" "the written directory is missing from the commit"))
            | Some d -> return! readFiles "C" s a.InvestigationId d
    }
    |> Remote.map (fun read ->
        match read with
        | Ok back when back.Events = a.Files.Events && back.Manifest = a.Files.Manifest && back.Snapshot = a.Files.Snapshot ->
            Confirmed { Commit = c; Tree = nt; Files = a.Files }
        | Ok _ -> Unknown("CAUSA.WRITE_UNCONFIRMED", "the bytes read back from the commit differ from the bytes written")
        | Error f -> Unknown(f.Cause, "the confirmation read failed: " + Fault.describe f))

let private beforePatch (f: Fault) : Outcome =
    match f.Kind with
    | RateLimited _ | ServerError | NoResponse | NotDelivered | ObjectMissing | Unreadable | Truncated -> BackToPending f
    | NotFound when f.Phase = "O" -> BackToPending f // an object this client just created: replication lag
    | _ -> Failed f

/// CAUSA-PROV-13: steps 1–10 of one atomic append, with bounded rebases when only other paths moved (CAUSA-PROV-27 §3).
let publish (a: Attempt) : Remote<Outcome> =
    let s = a.Session
    let orFault (onFault: Fault -> Outcome) (m: Remote<Result<'x, Fault>>) (f: 'x -> Remote<Outcome>) : Remote<Outcome> =
        m |> Remote.bind (function Ok x -> f x | Error e -> Done(onFault e))
    let expected = shasOf a.Files
    let rec fromHead (round: int) (tc: string) : Remote<Outcome> =
        orFault beforePatch (at "R" s a.InvestigationId tc) (fun atBase ->
            if not (sameAs atBase.Dir (a.Base |> Option.map (fun b -> b.Files))) then changedAt a atBase
            else
                let build =
                    git {
                        let! m = Git.createBlob s a.Files.Manifest
                        let! e = Git.createBlob s a.Files.Events
                        let! sn = Git.createBlob s a.Files.Snapshot
                        let p = Workspace.prefix s.Workspace a.InvestigationId
                        let! nt = Git.createTree s atBase.Tree [ p + "events.ndjson", e; p + "manifest.json", m; p + "snapshot.json", sn ]
                        do! guard s a.InvestigationId atBase.Tree nt expected
                        let! c = Git.createCommit s a.Message nt tc
                        return nt, c
                    }
                orFault beforePatch build (fun (nt, c) ->
                    let key, value = a.Record { Commit = c; Tree = nt; Parent = tc; PatchSentAt = a.Now }
                    Next(
                        Keep(key, value),
                        function
                        | Answered _ ->
                            orFault beforePatch (Git.head "R" s (2 * round)) (fun now ->
                                if now <> tc then moved round atBase now
                                else Next(Git.patchRequest s c, patched round atBase nt tc c))
                        | reply -> Done(BackToPending(noAnswer "O" reply))
                    )))
    /// CAUSA-PROV-27: the head is no longer Tc. Unchanged investigation → rebase; changed → conflict.
    and moved (round: int) (atBase: AtCommit) (newHead: string) : Remote<Outcome> =
        orFault beforePatch (at "R" s a.InvestigationId newHead) (fun now ->
            let unchanged = (atBase.Dir |> Option.map (fun d -> d.DirSha)) = (now.Dir |> Option.map (fun d -> d.DirSha))
            if not unchanged then changedAt a now
            elif round >= maxRounds then Done(BackToPending(integrity NonFastForward "R" "branch-busy" "the branch kept moving; try again shortly"))
            else fromHead (round + 1) newHead)
    and patched (round: int) (atBase: AtCommit) (nt: string) (tc: string) (c: string) (reply: Reply) : Remote<Outcome> =
        match reply with
        | Answered(200, body, _) ->
            let landed =
                try
                    use doc = JsonDocument.Parse body
                    (child "object" doc.RootElement |> Result.bind (str "sha")) = Ok c
                with _ -> false
            if landed then confirm a c nt tc else Done(Unknown("patch-unreadable", "GitHub answered the ref update but not with the new commit"))
        | Answered(status, body, h) ->
            let f = classify "U" status body h
            match f.Kind with
            | NonFastForward -> orFault beforePatch (Git.head "R" s (2 * round + 1)) (moved round atBase)
            | ServerError | Unreadable -> Done(Unknown(f.Cause, Fault.describe f))
            | RateLimited _ | ObjectMissing -> Done(BackToPending f)
            | _ -> Done(Failed f)
        | NotSent r -> Done(BackToPending(noAnswer "U" (NotSent r)))
        | Unanswered r -> Done(Unknown("timeout-after-patch", "the ref update was sent but no answer came back (" + r + ")"))
    match Workspace.checkTarget s.Workspace a.InvestigationId with
    | Error e -> Done(Failed(integrity (if e.Code = Codes.pathInvalid then PathInvalid else PathFrozen) "R" e.Code e.Detail))
    | Ok _ ->
        match a.Base with
        | Some b when not (startsWith a.Files.Events b.Files.Events) ->
            // CONTRACT §9.3: a writer only ever appends to the confirmed history; nothing is sent.
            Done(Failed(integrity Unreadable "R" Codes.historyRewritten "the files to publish do not extend the confirmed history"))
        | _ -> orFault beforePatch (Git.head "R" s 0) (fromHead 1)

/// CAUSA-PROV-24: after an ambiguous PATCH, classify the remote without writing anything.
let resolve (a: Attempt) : Remote<Outcome> =
    let s = a.Session
    git {
        let! h = Git.head "C" s 0
        let! now = at "C" s a.InvestigationId h
        let! files =
            match now.Dir with
            | None -> Done(Ok None)
            | Some d -> readFiles "C" s a.InvestigationId d |> Remote.map (Result.map Some)
        return now, files
    }
    |> Remote.map (fun read ->
        match read with
        | Error f -> Unknown(f.Cause, "still unknown: " + Fault.describe f)
        | Ok(now, remote) ->
            let cb = a.Base |> Option.map (fun b -> b.Files.Events) |> Option.defaultValue [||]
            let r = remote |> Option.map (fun f -> f.Events)
            match remote, r with
            | Some f, Some events when events = a.Files.Events && f.Manifest = a.Files.Manifest && f.Snapshot = a.Files.Snapshot ->
                Confirmed { Commit = now.Commit; Tree = now.Tree; Files = f } // (a)
            | Some f, Some events when startsWith events a.Files.Events && events.Length > a.Files.Events.Length ->
                match a.Verify f with
                | Ok() -> Confirmed { Commit = now.Commit; Tree = now.Tree; Files = a.Files } // (b): ours landed, others appended after
                | Error e -> Conflicted(e.Code, "GitHub's copy fails verification: " + e.Detail, None)
            | None, _ when cb.Length = 0 -> BackToPending(integrity NotDelivered "C" "not-landed" "the write did not land") // (c), create
            | Some _, Some events when events = cb && a.Base.IsSome -> BackToPending(integrity NotDelivered "C" "not-landed" "the write did not land") // (c)
            | Some f, Some events ->
                let copy = { Commit = now.Commit; Tree = now.Tree; Files = f }
                if not (startsWith events cb) then Conflicted(Codes.historyRewritten, "GitHub's events.ndjson no longer starts with the confirmed history", Some copy)
                else Conflicted(Codes.versionStale, "someone else appended to this investigation and this write did not land", Some copy)
            | _ -> Conflicted(Codes.historyRewritten, "the investigation is no longer on the branch", None))

/// Read one investigation at the current head (to adopt GitHub's copy or recover it on another device).
let load (s: Session) (iid: string) (verify: InvestigationFiles -> Result<unit, CausaError>) : Remote<Result<Confirmed option, Fault>> =
    match Workspace.checkTarget s.Workspace iid with
    | Error e -> Done(Error(integrity PathInvalid "R" e.Code e.Detail))
    | Ok _ ->
        git {
            let! h = Git.head "R" s 0
            let! now = at "R" s iid h
            match now.Dir with
            | None -> return None
            | Some d ->
                let! files = readFiles "R" s iid d
                match verify files with
                | Ok() -> return Some { Commit = now.Commit; Tree = now.Tree; Files = files }
                | Error e -> return! Done(Error(integrity Unreadable "R" e.Code (e.Code + ": " + e.Detail)))
        }

/// CAUSA-PROV-08: the investigation directories of the configured run, every entry reported, nothing repaired.
let list (s: Session) : Remote<Result<(string * Result<unit, string>) list, Fault>> =
    git {
        let! h = Git.head "R" s 0
        let! c = Git.commit "R" s h
        let! dir = walk "R" s c.Tree [ "workspaces"; "demo"; s.Workspace.RunId; "investigations" ]
        return
            dir
            |> Option.map (fun d ->
                d.Entries
                |> List.map (fun e ->
                    e.Name,
                    if not (Workspace.investigationIdValid e.Name) || e.Type <> "tree" then Error Codes.pathInvalid else Ok()))
            |> Option.defaultValue []
    }

/// CAUSA-PROV-18 §2: audit metadata only; never a token, personal data or domain text.
let commitMessage (w: Workspace) (iid: string) (fromVersion: int64) (toVersion: int64) (events: int) (workingCopyOf: string option) =
    "causa: " + w.RunId + "/" + iid + " v" + string fromVersion + " -> v" + string toVersion + " (" + string events + " events)"
    + (match workingCopyOf with Some d -> ", working copy of " + d | None -> "")

// ---------------------------------------------------------------------------------------------------------------------
// The durable record (CAUSA-PROV-25, adapter-private; never contains the token)
// ---------------------------------------------------------------------------------------------------------------------

type LogEntry =
    { Status: SyncState
      At: string
      Cause: string
      Detail: string }

type Record =
    { Workspace: Workspace
      InvestigationId: string
      Status: SyncState
      Confirmed: Confirmed option
      /// The files of the attempt in flight or last attempted (L = their events beyond the confirmed copy).
      InFlight: InvestigationFiles option
      Publish: Marker option
      /// GitHub's verified copy when a conflict was found, for the person to compare and choose.
      Remote: Confirmed option
      Log: LogEntry list
      LastError: string option }

[<RequireQualifiedAccess>]
module Record =
    let key (iid: string) = "causa:github:" + iid

    let create (w: Workspace) (iid: string) =
        { Workspace = w; InvestigationId = iid; Status = SyncPending; Confirmed = None; InFlight = None; Publish = None; Remote = None; Log = []; LastError = None }

    let transition (status: SyncState) (at: string) (cause: string) (detail: string) (r: Record) =
        { r with Status = status; Log = r.Log @ [ { Status = status; At = at; Cause = cause; Detail = detail } ] |> List.rev |> List.truncate 50 |> List.rev }

    let private filesJson (f: InvestigationFiles) =
        let o = JsonObject()
        o["directoryId"] <- JsonValue.Create f.DirectoryId
        o["manifest"] <- JsonValue.Create(Convert.ToBase64String f.Manifest)
        o["events"] <- JsonValue.Create(Convert.ToBase64String f.Events)
        o["snapshot"] <- JsonValue.Create(Convert.ToBase64String f.Snapshot)
        o

    let private confirmedJson (c: Confirmed) =
        let o = JsonObject()
        o["commit"] <- JsonValue.Create c.Commit
        o["tree"] <- JsonValue.Create c.Tree
        o["files"] <- filesJson c.Files
        o

    let encode (r: Record) : string =
        let o = JsonObject()
        o["recordVersion"] <- JsonValue.Create 1
        let w = JsonObject()
        w["owner"] <- JsonValue.Create(r.Workspace.Owner.ToLowerInvariant())
        w["repo"] <- JsonValue.Create(r.Workspace.Repo.ToLowerInvariant())
        w["branch"] <- JsonValue.Create r.Workspace.Branch
        w["runId"] <- JsonValue.Create r.Workspace.RunId
        o["workspace"] <- w
        o["investigationId"] <- JsonValue.Create r.InvestigationId
        o["status"] <- JsonValue.Create(SyncState.wire r.Status)
        o["confirmed"] <- (r.Confirmed |> Option.map (confirmedJson >> fun x -> x :> JsonNode) |> Option.toObj)
        o["inFlight"] <- (r.InFlight |> Option.map (filesJson >> fun x -> x :> JsonNode) |> Option.toObj)
        o["remote"] <- (r.Remote |> Option.map (confirmedJson >> fun x -> x :> JsonNode) |> Option.toObj)
        o["publish"] <-
            (r.Publish
             |> Option.map (fun m ->
                 let p = JsonObject()
                 p["commit"] <- JsonValue.Create m.Commit
                 p["tree"] <- JsonValue.Create m.Tree
                 p["parent"] <- JsonValue.Create m.Parent
                 p["patchSentAt"] <- JsonValue.Create m.PatchSentAt
                 p :> JsonNode)
             |> Option.toObj)
        let log = JsonArray()
        r.Log
        |> List.iter (fun e ->
            let x = JsonObject()
            x["status"] <- JsonValue.Create(SyncState.wire e.Status)
            x["at"] <- JsonValue.Create e.At
            x["cause"] <- JsonValue.Create e.Cause
            x["detail"] <- JsonValue.Create e.Detail
            log.Add x)
        o["statusLog"] <- log
        o["lastError"] <- (r.LastError |> Option.map (fun e -> nonNull (JsonValue.Create e) :> JsonNode) |> Option.toObj)
        o.ToJsonString()

    let decode (text: string) : Result<Record, string> =
        try
            use doc = JsonDocument.Parse text
            let r = doc.RootElement
            let s (e: JsonElement) (name: string) = match e.TryGetProperty name with | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj |> Option.defaultValue "" | _ -> ""
            let opt (name: string) (f: JsonElement -> 'a) = match r.TryGetProperty name with | true, v when v.ValueKind = JsonValueKind.Object -> Some(f v) | _ -> None
            let files (e: JsonElement) =
                { DirectoryId = s e "directoryId"; Manifest = Convert.FromBase64String(s e "manifest"); Events = Convert.FromBase64String(s e "events"); Snapshot = Convert.FromBase64String(s e "snapshot") }
            let confirmed (e: JsonElement) = { Commit = s e "commit"; Tree = s e "tree"; Files = files (e.GetProperty "files") }
            let w = r.GetProperty "workspace"
            match (match r.TryGetProperty "recordVersion" with | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32() | _ -> 0), SyncState.ofWire (s r "status") with
            | 1, Some status ->
                Ok
                    { Workspace = { Owner = s w "owner"; Repo = s w "repo"; Branch = s w "branch"; RunId = s w "runId" }
                      InvestigationId = s r "investigationId"
                      Status = status
                      Confirmed = opt "confirmed" confirmed
                      InFlight = opt "inFlight" files
                      Remote = opt "remote" confirmed
                      Publish = opt "publish" (fun p -> { Commit = s p "commit"; Tree = s p "tree"; Parent = s p "parent"; PatchSentAt = s p "patchSentAt" })
                      Log =
                        match r.TryGetProperty "statusLog" with
                        | true, xs when xs.ValueKind = JsonValueKind.Array ->
                            [ for x in xs.EnumerateArray() ->
                                  { Status = SyncState.ofWire (s x "status") |> Option.defaultValue SyncPending; At = s x "at"; Cause = s x "cause"; Detail = s x "detail" } ]
                        | _ -> []
                      LastError = match r.TryGetProperty "lastError" with | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj | _ -> None }
            | _ -> Error "unsupported record"
        with ex -> Error ex.Message

    /// CAUSA-PROV-26 §2 (T19/T20): after a restart, `sending` is `unknown` if the PATCH may have been sent.
    let recover (at: string) (r: Record) =
        match r.Status, r.Publish with
        | SyncSending, Some _ -> transition SyncUnknown at "restart-after-patch-marker" "the app restarted after the ref update may have been sent" r
        | SyncSending, None -> transition SyncPending at "restart-before-patch" "the app restarted before anything was published" r
        | _ -> r
