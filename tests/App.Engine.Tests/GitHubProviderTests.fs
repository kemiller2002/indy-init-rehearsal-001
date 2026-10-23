/// Provider gate rows (PROVIDER-GITHUB.md §10.2, AC-PROV-*) for the Causa GitHub provider, run against the fake GitHub
/// of §10.1 with the real frozen investigations. Rows this provider does not implement are not claimed here (see
/// evidence/causa-integration/PERSISTENCE-VALIDATION.md).
module App.Engine.Tests.GitHubProviderTests

#nowarn "3261"

open System
open System.Text
open Xunit
open IndyInitRootCauseInvestigator.Engine
open IndyInitRootCauseInvestigator.Engine.CausaGitHub
open Causa.Contract
open App.Engine.Tests.FakeGitHub

let private token = "github_pat_TESTtoken0123456789abcdef"
let private branch = "causa-workspace"
let private ws = { Owner = "kemiller2002"; Repo = "indy-init-rehearsal-001"; Branch = branch; RunId = "test-20261023-120000-abcdef" }
let private session = { Workspace = ws; Token = token; ApiBase = apiBase; Nonce = "n" }

let private ds = lazy TestData.dataset.Value
let private frozen key = ds.Value.Valid |> List.find (fun v -> v.ScenarioKey = key)
let private verify (f: InvestigationFiles) = Store.validate ds.Value.Schemas f |> Result.map ignore

let private filesOf (key: string) =
    match Store.build ds.Value.Schemas (frozen key).Loaded.Events with
    | Ok(f, _) -> f
    | Error e -> failwith e.Detail

/// The frozen investigation plus one observation (text with non-ASCII, U+2028 and a 4-byte character).
let private appendedOf (key: string) (text: string) =
    let v = frozen key
    let env =
        { CommandId = Guid "9b1c2d6e-0000-4000-8000-0000000000c2"; Investigation = Id.create v.Loaded.InvestigationId; ExpectedVersion = Some v.Loaded.State.Version
          Actor = Actor.human "local-user"; RequestedAt = DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero)
          Command = RecordObservation(Id.create (Guid "9b1c2d6e-0000-4000-8000-0000000000a2"), text, None) }
    match Decide.execute (Some v.Loaded.State) env with
    | Error es -> failwith (es |> List.map (fun e -> e.Code) |> String.concat ", ")
    | Ok events ->
        match Store.append ds.Value.Schemas v.Loaded v.Loaded.State.Version events with
        | Ok(f, _) -> f
        | Error e -> failwith e.Detail

let private iidOf key = Uuid.text (frozen key).Loaded.InvestigationId
let private prefixOf key = Workspace.prefix ws (iidOf key)

let private seeded () =
    let s = empty token
    let s, _ = commitFiles branch "operator: data branch" [ ".gitattributes", Encoding.ASCII.GetBytes "* -text\n"; "fixtures/v1.0.0/manifest.json", Encoding.ASCII.GetBytes "{\"frozen\":true}\n" ] s
    s

let private attempt (key: string) (atBase: Confirmed option) (files: InvestigationFiles) =
    { Session = session
      InvestigationId = iidOf key
      Base = atBase
      Files = files
      Message = commitMessage ws (iidOf key) 0L 1L 1 None
      Now = "2026-10-23T12:00:00.000Z"
      Verify = verify
      Record = fun m -> Record.key (iidOf key), "{\"publish\":{\"commit\":\"" + m.Commit + "\",\"parent\":\"" + m.Parent + "\"}}" }

let private confirmed (o: Outcome) =
    match o with
    | Outcome.Confirmed c -> c
    | other -> failwithf "expected confirmed, got %A" other

let private head (s: Server) = s.Refs[branch]
let private patches (s: Server) = s.Log |> List.filter (fun r -> r.Method = "PATCH")
let private kinds (s: Server) =
    s.Log |> List.map (fun r -> r.Method + " " + ((Uri r.Url).AbsolutePath.Split("/git/")[1]).Split("/")[0])

/// Publish the frozen copy of `key` (a working copy, CAUSA-PROV-11) and return the confirmed copy and server.
let private published (key: string) (s: Server) =
    let outcome, s = run s (publish (attempt key None (filesOf key)))
    confirmed outcome, s

[<Fact>]
let ``[AC-PROV-01] a create is one commit on the old head adding exactly P, confirmed only after the SHA re-read, in the CAUSA-PROV-13 order`` () =
    let s0 = seeded ()
    let before = head s0
    let outcome, s = run s0 (publish (attempt "api-latency" None (filesOf "api-latency")))
    let c = confirmed outcome
    Assert.Equal(c.Commit, head s)
    Assert.Equal<string list>([ before ], parentsOf s c.Commit)
    Assert.Equal(1, commitCount s - commitCount s0)
    for name in Store.fileNames do
        Assert.True((fileAt s branch (prefixOf "api-latency" + name)).IsSome)
    // Everything outside P is untouched.
    Assert.Equal(dirAt s0 before "fixtures", dirAt s c.Commit "fixtures")
    Assert.Equal<byte[]>((fileAt s0 branch ".gitattributes").Value, (fileAt s branch ".gitattributes").Value)
    let order = kinds s
    let patchAt = order |> List.findIndex (fun k -> k.StartsWith "PATCH")
    Assert.Equal<string list>([ "GET ref"; "GET commits"; "GET trees" ], order |> List.take 3)
    Assert.Equal<string list>([ "POST blobs"; "POST blobs"; "POST blobs"; "POST trees" ], order |> List.filter (fun k -> k.StartsWith "POST") |> List.take 4)
    Assert.Equal("GET ref", order[patchAt - 1])
    Assert.Equal("POST commits", order |> List.take patchAt |> List.filter (fun k -> k.StartsWith "POST") |> List.last)
    Assert.Equal("GET commits", order[patchAt + 1])
    Assert.True(order |> List.skip (patchAt + 1) |> List.forall (fun k -> k.StartsWith "GET"))
    // The step-7 marker was written before the PATCH.
    Assert.Single s.KeptLog |> ignore
    // The bytes are the frozen bytes, byte for byte (CAUSA-PROV-11 §2).
    let original = TestData.releaseFiles["fixtures/v1.0.0/investigations/" + iidOf "api-latency" + "/events.ndjson"]
    Assert.Equal<byte[]>(original, (fileAt s branch (prefixOf "api-latency" + "events.ndjson")).Value)

[<Fact>]
let ``[AC-PROV-02] [AC-PROV-20] an append changes exactly the three files, keeps the old events as a byte prefix and round-trips non-ASCII bytes`` () =
    let c1, s = published "api-latency" (seeded ())
    let next = appendedOf "api-latency" "Latency back to normal — 東京   line 𝄞 \u0001 done"
    let outcome, s = run s (publish (attempt "api-latency" (Some c1) next))
    let c2 = confirmed outcome
    Assert.Equal<string list>([ c1.Commit ], parentsOf s c2.Commit)
    let events = (fileAt s branch (prefixOf "api-latency" + "events.ndjson")).Value
    Assert.True(events.AsSpan().StartsWith(c1.Files.Events.AsSpan()))
    Assert.Equal<byte[]>(next.Events, events)
    let back = { next with Manifest = (fileAt s branch (prefixOf "api-latency" + "manifest.json")).Value; Snapshot = (fileAt s branch (prefixOf "api-latency" + "snapshot.json")).Value; Events = events }
    Assert.Equal(Ok(), verify back)

[<Fact>]
let ``[AC-PROV-06] another writer on a different path between the base read and the PATCH: rebase with the same blobs, both changes kept`` () =
    let s0 = seeded ()
    let other (s: Server) = commitFiles branch "someone else" [ "notes/other.txt", Encoding.ASCII.GetBytes "other\n" ] s |> fst
    // The second ref read (step 8) sees the moved head.
    let s0 = s0 |> withHook (fun r -> r.Method = "GET" && r.Url.Contains("/git/ref/") && r.Url.Contains("nocache=n-2")) (Before other)
    let outcome, s = run s0 (publish (attempt "api-latency" None (filesOf "api-latency")))
    let c = confirmed outcome
    Assert.True((fileAt s branch "notes/other.txt").IsSome)
    Assert.True((fileAt s branch (prefixOf "api-latency" + "events.ndjson")).IsSome)
    Assert.Equal(c.Commit, head s)
    Assert.Single(patches s) |> ignore
    Assert.Equal(2, s.Log |> List.filter (fun r -> r.Method = "POST" && r.Url.EndsWith "/git/commits") |> List.length)

[<Fact>]
let ``[AC-PROV-03 partial] another writer appended to the same investigation: no PATCH from the stale base, the record is conflicted with GitHub's verified copy`` () =
    let c1, s = published "api-latency" (seeded ())
    let theirs = appendedOf "api-latency" "Their observation"
    let ours = appendedOf "api-latency" "Our observation"
    let s = commitFiles branch "other writer" [ prefixOf "api-latency" + "events.ndjson", theirs.Events; prefixOf "api-latency" + "manifest.json", theirs.Manifest; prefixOf "api-latency" + "snapshot.json", theirs.Snapshot ] s |> fst
    let before = head s
    let patchesBefore = (patches s).Length
    let outcome, s = run s (publish (attempt "api-latency" (Some c1) ours))
    match outcome with
    | Outcome.Conflicted(cause, _, Some remote) ->
        Assert.Equal(Codes.versionStale, cause)
        Assert.Equal<byte[]>(theirs.Events, remote.Files.Events)
    | other -> failwithf "expected conflicted, got %A" other
    Assert.Equal(before, head s)
    Assert.Equal(patchesBefore, (patches s).Length)

[<Fact>]
let ``[AC-PROV-07] the PATCH is applied and its answer lost: unknown, then resolution by SHA reads confirms without a second commit`` () =
    let s0 = seeded () |> withHook (fun r -> r.Method = "PATCH") ApplyThenDrop
    let outcome, s = run s0 (publish (attempt "api-latency" None (filesOf "api-latency")))
    match outcome with
    | Outcome.Unknown(cause, _) -> Assert.Equal("timeout-after-patch", cause)
    | other -> failwithf "expected unknown, got %A" other
    let commits = commitCount s
    let resolved, s = run s (resolve (attempt "api-latency" None (filesOf "api-latency")))
    confirmed resolved |> ignore
    Assert.Equal(commits, commitCount s)
    Assert.Single(patches s) |> ignore

[<Fact>]
let ``[AC-PROV-08] the PATCH is lost before it applies: unknown, resolution finds nothing landed, the retry lands exactly one commit`` () =
    let s0 = seeded () |> withHook (fun r -> r.Method = "PATCH") DropUnapplied
    let before = head s0
    let outcome, s = run s0 (publish (attempt "api-latency" None (filesOf "api-latency")))
    Assert.True(match outcome with Outcome.Unknown _ -> true | _ -> false)
    Assert.Equal(before, head s)
    let resolved, s = run s (resolve (attempt "api-latency" None (filesOf "api-latency")))
    Assert.True(match resolved with Outcome.BackToPending _ -> true | _ -> false)
    let retried, s = run s (publish (attempt "api-latency" None (filesOf "api-latency")))
    let c = confirmed retried
    Assert.Equal<string list>([ before ], parentsOf s c.Commit)

[<Fact>]
let ``[AC-PROV-09] [AC-PROV-16] a restart after the step-7 marker is unknown (T20); before it, pending (T19)`` () =
    let r = Record.create ws (iidOf "api-latency")
    let sending = { r with Status = SyncSending; Publish = Some { Commit = "c"; Tree = "t"; Parent = "p"; PatchSentAt = "x" } }
    let back = Record.decode (Record.encode sending) |> Result.map (Record.recover "later")
    Assert.Equal(Ok SyncUnknown, back |> Result.map (fun x -> x.Status))
    let early = Record.decode (Record.encode { r with Status = SyncSending }) |> Result.map (Record.recover "later")
    Assert.Equal(Ok SyncPending, early |> Result.map (fun x -> x.Status))

[<Fact>]
let ``[AC-PROV-10] a failure after the objects are created leaves the head unchanged and the record pending; a retry confirms`` () =
    let s0 = seeded () |> withHook (fun r -> r.Method = "POST" && r.Url.EndsWith "/git/commits") (Respond(502, "{\"message\":\"Bad Gateway\"}", Map.empty))
    let before = head s0
    let outcome, s = run s0 (publish (attempt "api-latency" None (filesOf "api-latency")))
    Assert.True(match outcome with Outcome.BackToPending _ -> true | _ -> false)
    Assert.Equal(before, head s)
    Assert.Empty(patches s)
    confirmed (fst (run s (publish (attempt "api-latency" None (filesOf "api-latency"))))) |> ignore

[<Fact>]
let ``[AC-PROV-11] a tree that changes a frozen path is refused by the guard with CAUSA.PATH_FROZEN and no PATCH`` () =
    let a = attempt "api-latency" None (filesOf "api-latency")
    let p = prefixOf "api-latency"
    let s = seeded ()
    let before = head s
    // A misbehaving server answers the tree request with a tree that adds P *and* rewrites a frozen file.
    let s, evil =
        commitFiles "scratch" "evil"
            [ "fixtures/v1.0.0/manifest.json", Encoding.ASCII.GetBytes "{\"frozen\":false}\n"
              p + "events.ndjson", a.Files.Events; p + "manifest.json", a.Files.Manifest; p + "snapshot.json", a.Files.Snapshot ]
            { s with Refs = s.Refs |> Map.add "scratch" before }
    let evilTree = match Map.find evil s.Objects with Commit(t, _, _) -> t | _ -> ""
    let s = s |> withHook (fun r -> r.Method = "POST" && r.Url.EndsWith "/git/trees") (Respond(201, "{\"sha\":\"" + evilTree + "\"}", Map.empty))
    let outcome, s = run s (publish a)
    match outcome with
    | Outcome.Failed f ->
        Assert.Equal(PathFrozen, f.Kind)
        Assert.Equal("CAUSA.PATH_FROZEN", f.Cause)
    | other -> failwithf "expected failed, got %A" other
    Assert.Empty(patches s)
    Assert.Equal(before, head s)

[<Theory>]
[<InlineData("Run-1")>]
[<InlineData("../x")>]
[<InlineData("a2345678901234567890123456789012345678901234567890123456789012345")>]
let ``[AC-PROV-12] an invalid runId is CAUSA.PATH_INVALID with zero requests`` (runId: string) =
    let bad = { session with Workspace = { ws with RunId = runId } }
    let outcome, s = run (seeded ()) (publish { attempt "api-latency" None (filesOf "api-latency") with Session = bad })
    match outcome with
    | Outcome.Failed f -> Assert.Equal(Codes.pathInvalid, f.Cause)
    | other -> failwithf "expected failed, got %A" other
    Assert.Empty s.Log

[<Fact>]
let ``[AC-PROV-12] a non-canonical investigation id is CAUSA.PATH_INVALID with zero requests`` () =
    let outcome, s = run (seeded ()) (publish { attempt "api-latency" None (filesOf "api-latency") with InvestigationId = "D588748E-8DF9-5F27-859E-5D36D9A2B53D" })
    Assert.True(match outcome with Outcome.Failed f -> f.Cause = Codes.pathInvalid | _ -> false)
    Assert.Empty s.Log

[<Fact>]
let ``[AC-PROV-13] the token appears only in Authorization headers to the API origin`` () =
    let c1, s = published "api-latency" (seeded ())
    let s = s |> withHook (fun r -> r.Method = "PATCH") ApplyThenDrop
    let _, s = run s (publish (attempt "api-latency" (Some c1) (appendedOf "api-latency" "token scan")))
    let _, s = run s (resolve (attempt "api-latency" (Some c1) (appendedOf "api-latency" "token scan")))
    let forms = [ token; Convert.ToBase64String(Encoding.UTF8.GetBytes token); Uri.EscapeDataString token ]
    let leaks (text: string) = forms |> List.exists (fun f -> text.Contains(f, StringComparison.Ordinal))
    for r in s.Log do
        Assert.False(leaks r.Url)
        Assert.False(leaks (r.Body |> Option.defaultValue ""))
        Assert.StartsWith(apiBase + "/", r.Url)
        for (k, v) in r.Headers do
            if k <> "Authorization" then Assert.False(leaks v)
    for (_, v) in s.KeptLog do Assert.False(leaks v)
    for o in s.Objects |> Map.toList |> List.map snd do
        match o with
        | Blob b -> Assert.False(leaks (Encoding.UTF8.GetString b))
        | Commit(_, _, m) -> Assert.False(leaks m)
        | Tree _ -> ()
    let record = { Record.create ws (iidOf "api-latency") with Confirmed = Some c1; LastError = Some "x" }
    Assert.False(leaks (Record.encode record))

[<Fact>]
let ``[AC-PROV-14] a create whose directory already exists is refused with CAUSA.VERSION_STALE and nothing is written`` () =
    // Someone else's create is already there: the frozen history plus their own event.
    let theirs = appendedOf "api-latency" "their create"
    let _, s = run (seeded ()) (publish (attempt "api-latency" None theirs))
    let existing = (fileAt s branch (prefixOf "api-latency" + "events.ndjson")).Value
    let before = head s
    let different = appendedOf "api-latency" "a different create"
    let patchesBefore = (patches s).Length
    let outcome, s = run s (publish (attempt "api-latency" None different))
    match outcome with
    | Outcome.Refused(cause, _) -> Assert.Equal(Codes.versionStale, cause)
    | other -> failwithf "expected refused, got %A" other
    Assert.Equal(before, head s)
    Assert.Equal<byte[]>(existing, (fileAt s branch (prefixOf "api-latency" + "events.ndjson")).Value)
    Assert.Equal(patchesBefore, (patches s).Length)

[<Fact>]
let ``[CAUSA-PROV-10 §4] [CAUSA-PROV-24 (c)] this client's own earlier write that landed late is built on, not refused or overwritten`` () =
    // An earlier create of ours landed although its outcome was lost (the frozen copy); the next attempt still has no
    // confirmed copy and carries one more event. GitHub's copy is a verified byte prefix of it: publishing continues.
    let _, s = published "api-latency" (seeded ())
    let first = head s
    let next = appendedOf "api-latency" "after a lost confirmation"
    let outcome, s = run s (publish (attempt "api-latency" None next))
    let c = confirmed outcome
    Assert.Equal<string list>([ first ], parentsOf s c.Commit)
    Assert.Equal<byte[]>(next.Events, (fileAt s branch (prefixOf "api-latency" + "events.ndjson")).Value)

[<Fact>]
let ``[CAUSA-PROV-29 §5] a rewritten history is conflicted without an adoptable copy`` () =
    let c1, s = published "api-latency" (seeded ())
    // Someone rewinds GitHub's copy to a shorter, still valid history: the confirmed events are no longer its prefix.
    let shorter =
        match Store.build ds.Value.Schemas ((frozen "api-latency").Loaded.Events |> List.take 10) with
        | Ok(f, _) -> f
        | Error e -> failwith e.Detail
    let p = prefixOf "api-latency"
    let s, _ = commitFiles branch "rewrite" [ p + "events.ndjson", shorter.Events; p + "manifest.json", shorter.Manifest; p + "snapshot.json", shorter.Snapshot ] s
    let before = head s
    let outcome, s = run s (publish (attempt "api-latency" (Some c1) (appendedOf "api-latency" "ours")))
    match outcome with
    | Outcome.Conflicted(cause, _, remote) ->
        Assert.Equal(Codes.historyRewritten, cause)
        Assert.True(remote.IsNone) // nothing to adopt: only discard, export or a new run (CAUSA-PROV-29 §5)
    | other -> failwithf "expected conflicted, got %A" other
    Assert.Equal(before, head s)

[<Fact>]
let ``[AC-PROV-15 partial] working copies of all five frozen investigations: every copied file equals the frozen bytes and frozen trees are unchanged`` () =
    let s0 = seeded ()
    let fixturesBefore = dirAt s0 (head s0) "fixtures"
    let s =
        ds.Value.Valid
        |> List.fold (fun s v -> snd (published v.ScenarioKey s)) s0
    for v in ds.Value.Valid do
        let id = Uuid.text v.Loaded.InvestigationId
        let dir = "fixtures/v1.0.0/investigations/" + id + "/"
        for name in Store.fileNames do
            Assert.Equal<byte[]>(TestData.releaseFiles[dir + name], (fileAt s branch (prefixOf v.ScenarioKey + name)).Value)
    Assert.Equal(fixturesBefore, dirAt s (head s) "fixtures")
    // One commit per investigation, not one for all five (deviation recorded in PERSISTENCE-VALIDATION.md).
    Assert.Equal(5, commitCount s - commitCount s0)

[<Theory>]
[<InlineData(401, "{\"message\":\"Bad credentials\"}", "", "failed")>]
[<InlineData(403, "{\"message\":\"API rate limit exceeded\"}", "x-ratelimit-remaining=0", "pending")>]
[<InlineData(429, "{\"message\":\"slow down\"}", "retry-after=30", "pending")>]
[<InlineData(422, "{\"message\":\"Reference does not exist\"}", "", "failed")>]
[<InlineData(409, "{\"message\":\"Git Repository is empty.\"}", "", "failed")>]
[<InlineData(502, "{\"message\":\"Bad Gateway\"}", "", "unknown")>]
let ``[AC-PROV-17] responses at the PATCH map to the CAUSA-PROV-19 states`` (status: int, body: string, header: string, expected: string) =
    let headers = if header = "" then Map.empty else Map.ofList [ header.Split('=')[0], header.Split('=')[1] ]
    let s0 = seeded () |> withHook (fun r -> r.Method = "PATCH") (Respond(status, body, headers))
    let before = head s0
    let outcome, s = run s0 (publish (attempt "api-latency" None (filesOf "api-latency")))
    let state =
        match outcome with
        | Outcome.Failed _ -> "failed"
        | Outcome.BackToPending _ -> "pending"
        | Outcome.Unknown _ -> "unknown"
        | Outcome.Conflicted _ -> "conflicted"
        | other -> sprintf "%A" other
    Assert.Equal(expected, state)
    Assert.Equal(before, head s)

[<Fact>]
let ``[AC-PROV-18] a rewind between commit creation and the PATCH is caught by the step-8 re-read; lost confirmed events are CAUSA.HISTORY_REWRITTEN`` () =
    let s0 = seeded ()
    let seed = head s0
    let c1, s = published "api-latency" s0
    let rewind (s: Server) = { s with Refs = s.Refs |> Map.add branch seed }
    let s = s |> withHook (fun r -> r.Method = "GET" && r.Url.Contains("/git/ref/") && r.Url.Contains("nocache=n-2")) (Before rewind)
    let outcome, s = run s (publish (attempt "api-latency" (Some c1) (appendedOf "api-latency" "after rewind")))
    match outcome with
    | Outcome.Conflicted(cause, _, _) -> Assert.Equal(Codes.historyRewritten, cause)
    | other -> failwithf "expected conflicted, got %A" other
    Assert.Empty(patches s |> List.skip 1)
    Assert.Equal(seed, head s)

[<Fact>]
let ``[AC-PROV-19] a truncated tree listing is never taken as complete`` () =
    let truncated (body: string) = body.Replace("\"truncated\":false", "\"truncated\":true")
    let s0 = seeded () |> withHook (fun r -> r.Method = "GET" && r.Url.Contains("/git/trees/")) (AlterReply truncated)
    let outcome, s = run s0 (publish (attempt "api-latency" None (filesOf "api-latency")))
    match outcome with
    | Outcome.BackToPending f -> Assert.Equal(Truncated, f.Kind)
    | other -> failwithf "expected pending, got %A" other
    Assert.Empty(patches s)

[<Fact>]
let ``[AC-PROV-20] a blob GitHub stores differently is a byte mismatch before any PATCH`` () =
    let s0 = seeded () |> withHook (fun r -> r.Method = "POST" && r.Url.EndsWith "/git/blobs") (Respond(201, "{\"sha\":\"0000000000000000000000000000000000000000\"}", Map.empty))
    let outcome, s = run s0 (publish (attempt "api-latency" None (filesOf "api-latency")))
    match outcome with
    | Outcome.Failed f -> Assert.Equal(ByteMismatch, f.Kind)
    | other -> failwithf "expected failed, got %A" other
    Assert.Empty(patches s)

[<Fact>]
let ``[AC-PROV-21] confirmation and resolution never use the Contents API or raw.githubusercontent.com`` () =
    let c1, s = published "api-latency" (seeded ())
    let s = s |> withHook (fun r -> r.Method = "PATCH") ApplyThenDrop
    // The shell gives every program run its own nonce.
    let run2 nonce (a: Attempt) = { a with Session = { a.Session with Nonce = nonce } }
    let _, s = run s (publish (run2 "p2" (attempt "api-latency" (Some c1) (appendedOf "api-latency" "x"))))
    let _, s = run s (resolve (run2 "r1" (attempt "api-latency" (Some c1) (appendedOf "api-latency" "x"))))
    Assert.DoesNotContain(s.Log, fun r -> r.Url.Contains("/contents/") || r.Url.Contains("raw.githubusercontent.com"))
    // Only ref reads are branch-addressed, and each is a distinct (uncached) URL.
    let refReads = s.Log |> List.filter (fun r -> r.Url.Contains("/git/ref/")) |> List.map (fun r -> r.Url)
    Assert.Equal(refReads.Length, (List.distinct refReads).Length)

[<Fact>]
let ``[AC-PROV-22] a run lists every entry; invalid names, a fourth file and a bad events digest are reported, never repaired`` () =
    let _, s = published "api-latency" (seeded ())
    let p = "workspaces/demo/" + ws.RunId + "/investigations/"
    let other = filesOf "payment-failure"
    let otherId = iidOf "payment-failure"
    let sso = filesOf "authentication-outage"
    let ssoId = iidOf "authentication-outage"
    // Still well-formed and canonical, but no longer the bytes the manifest's eventsSha256 names.
    let corrupted = Encoding.UTF8.GetBytes((Encoding.UTF8.GetString sso.Events).Replace("\"aggregateVersion\":3,", "\"aggregateVersion\":4,"))
    let s, _ =
        commitFiles branch "invalid entries"
            [ p + "Not-A-Uuid/manifest.json", other.Manifest
              p + otherId + "/manifest.json", other.Manifest
              p + otherId + "/events.ndjson", other.Events
              p + otherId + "/snapshot.json", other.Snapshot
              p + otherId + "/extra.txt", Encoding.ASCII.GetBytes "x"
              p + ssoId + "/manifest.json", sso.Manifest
              p + ssoId + "/events.ndjson", corrupted
              p + ssoId + "/snapshot.json", sso.Snapshot ]
            s
    let writesBefore = s.Log |> List.filter (fun r -> r.Method <> "GET") |> List.length
    let listed, s = run s (list session)
    match listed with
    | Ok entries ->
        Assert.Equal(Some(Error Codes.pathInvalid), entries |> List.tryFind (fun (n, _) -> n = "Not-A-Uuid") |> Option.map snd)
        Assert.Equal(Some(Ok()), entries |> List.tryFind (fun (n, _) -> n = iidOf "api-latency") |> Option.map snd)
    | Error f -> failwith (Fault.describe f)
    let loaded, s = run s (load session (iidOf "api-latency") verify)
    Assert.True(match loaded with Ok(Some _) -> true | _ -> false)
    let fourth, s = run s (load session otherId verify)
    Assert.True(match fourth with Error f -> f.Cause = Codes.pathInvalid | _ -> false)
    let badHash, s = run s (load session ssoId verify)
    Assert.True(match badHash with Error f -> f.Cause = Codes.eventsHashMismatch | _ -> false)
    Assert.Equal(writesBefore, s.Log |> List.filter (fun r -> r.Method <> "GET") |> List.length)

[<Fact>]
let ``[AC-PROV-24] the persisted actorId is the app's pseudonymous id, never the GitHub user`` () =
    let next = appendedOf "api-latency" "actor check"
    let text = Encoding.UTF8.GetString next.Events
    Assert.Contains("\"actorId\":\"local-user\"", text)
    Assert.DoesNotContain("kemiller2002", text)

[<Fact>]
let ``the workspace configuration rejects invalid owners, repositories, branches and run ids`` () =
    Assert.True(Workspace.check ws |> Result.isOk)
    Assert.True(Workspace.check { ws with Owner = "-bad" } |> Result.isError)
    Assert.True(Workspace.check { ws with Repo = ".." } |> Result.isError)
    Assert.True(Workspace.check { ws with Branch = "a..b" } |> Result.isError)
    Assert.True(Workspace.check { ws with Branch = "x.lock" } |> Result.isError)
    Assert.True(Workspace.check { ws with RunId = "ok-run\n" } |> Result.isError)
    let generated = Workspace.newRunId "ipad" (DateTimeOffset(2026, 10, 23, 14, 25, 1, TimeSpan.Zero)) (Guid "7f3a9c00-0000-4000-8000-000000000000")
    Assert.Equal("ipad-20261023-142501-009c3a", generated)
    Assert.True(Workspace.runIdValid generated)
