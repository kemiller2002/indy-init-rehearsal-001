module App.Engine.Tests.ShellTests

// JsonElement.GetString() is nullable; these tests assert on known-present strings.
#nowarn "3261"

open System
open System.Text.Json
open Xunit
open IndyInitRootCauseInvestigator.Engine
open IndyInitRootCauseInvestigator.Engine.Shell

// ---------------------------------------------------------------------------
// Driving the pure shell with real Limen wire messages
// ---------------------------------------------------------------------------

let private fingerprint = "sha256:2d5e16b7111fc78a319706b9927e4523cfcc519b7a2c9352ca8283ba32d6b71c"
let private lifecycleFingerprint = "sha256:cadd4037cac232869a486237ec523e667af9490cdcf3e4fb1ba729cbc5c87978"

let private initialize (hash: string) =
    let q (v: string) = JsonSerializer.Serialize v
    "{\"kind\":\"Initialize\",\"protocolVersion\":1,\"capabilities\":[\"Http\",\"Storage\",\"Clipboard\",\"Navigation\"],"
    + "\"location\":{\"origin\":\"https://kevinmmiller.us\",\"path\":\"/indy-init-rehearsal-001/\",\"query\":\"\",\"hash\":" + q hash + "},"
    + "\"handshake\":{\"protocol\":{\"major\":1,\"minor\":4},\"contract\":{\"unit\":\"limen.core\",\"version\":1,\"fingerprint\":" + q fingerprint + "},"
    + "\"capabilities\":[{\"id\":\"limen.lifecycle\",\"version\":1,\"fingerprint\":" + q lifecycleFingerprint + "}]}}"

let private event (name: string) (key: string option) (value: string option) =
    let key = key |> Option.map (fun k -> ",\"key\":" + JsonSerializer.Serialize k) |> Option.defaultValue ""
    let value = value |> Option.map (fun v -> ",\"value\":" + JsonSerializer.Serialize v) |> Option.defaultValue ""
    "{\"kind\":\"Event\",\"event\":{\"kind\":\"Event\",\"name\":" + JsonSerializer.Serialize name + key + value + "}}"

let private storageResult (correlation: string) (value: string option) =
    let v = value |> Option.map JsonSerializer.Serialize |> Option.defaultValue "null"
    "{\"kind\":\"EffectResult\",\"result\":{\"kind\":\"StorageResult\",\"correlationId\":\"" + correlation + "\",\"outcome\":{\"kind\":\"Success\",\"value\":" + v + "}}}"

let private httpSuccess (correlation: string) (status: int) (body: string) =
    "{\"kind\":\"EffectResult\",\"result\":{\"kind\":\"HttpResult\",\"correlationId\":\"" + correlation + "\",\"outcome\":{\"kind\":\"Success\",\"status\":"
    + string status + ",\"body\":" + JsonSerializer.Serialize body + "}}}"

let private httpNetworkFailure (correlation: string) =
    "{\"kind\":\"EffectResult\",\"result\":{\"kind\":\"HttpResult\",\"correlationId\":\"" + correlation + "\",\"outcome\":{\"kind\":\"Failure\",\"reason\":\"network\"}}}"

let private connectivity (online: bool) =
    "{\"kind\":\"CapabilityFact\",\"capability\":\"limen.lifecycle\",\"version\":1,\"fact\":{\"kind\":\"ConnectivityChanged\",\"subscription\":\"s1\",\"online\":" + (if online then "true" else "false") + "}}"

let mutable private tick = 0

/// One message through `handle`, with deterministic entropy and clock.
let private send (st: AppState) (message: string) : AppState * JsonDocument =
    tick <- tick + 1
    let stamped = withContext (Guid("6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f")) (DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero).AddMinutes(float tick)) st
    let next, json = handle stamped message
    next, JsonDocument.Parse json

let private effects (doc: JsonDocument) = [ for e in doc.RootElement.GetProperty("effects").EnumerateArray() -> e ]

let private kindOf (e: JsonElement) = e.GetProperty("kind").GetString()

let private view (doc: JsonDocument) = doc.RootElement.GetProperty("view")

let private text (doc: JsonDocument) (key: string) = (view doc).GetProperty(key).GetString()

/// Answer every storage read with "nothing stored".
let private answerLoads (st: AppState) (doc: JsonDocument) =
    effects doc
    |> List.filter (fun e -> kindOf e = "Storage" && e.GetProperty("operation").GetString() = "get")
    |> List.fold (fun (s, d) e -> send s (storageResult (e.GetProperty("correlationId").GetString()) None)) (st, doc)

let private boot (hash: string) =
    let st, doc = send initial (initialize hash)
    let st, _ = answerLoads st doc
    st, doc

let private start (st: AppState) =
    let st, _ = send st (event "f.observation" None (Some "Checkout returns 502 since 02:00."))
    let st, _ = send st (event "f.title" None (Some "Checkout 502"))
    send st (event "start" None None)

let private first (e: JsonElement) = e.EnumerateArray() |> Seq.head

let private viewKeys (doc: JsonDocument) = [ for p in (view doc).EnumerateObject() -> p.Name ] |> Set.ofList

// ---------------------------------------------------------------------------
// Wire codec
// ---------------------------------------------------------------------------

[<Fact>]
let ``Initialize is answered with exactly view, effects, cancellations and an exact handshake`` () =
    let _, doc = send initial (initialize "#/")
    let keys = [ for p in doc.RootElement.EnumerateObject() -> p.Name ]
    Assert.Equal<string list>([ "view"; "effects"; "cancellations"; "handshake" ], keys)
    let hs = doc.RootElement.GetProperty("handshake")
    Assert.Equal("Accepted", hs.GetProperty("kind").GetString())
    Assert.Equal(1, hs.GetProperty("protocol").GetProperty("major").GetInt32())
    Assert.Equal(4, hs.GetProperty("protocol").GetProperty("minor").GetInt32())
    Assert.Equal(fingerprint, hs.GetProperty("contract").GetProperty("fingerprint").GetString())
    Assert.Equal("limen.core", hs.GetProperty("contract").GetProperty("unit").GetString())
    let caps = [ for c in hs.GetProperty("capabilities").EnumerateArray() -> c.GetProperty("id").GetString(), c.GetProperty("fingerprint").GetString() ]
    Assert.Equal<(string * string) list>([ "limen.lifecycle", lifecycleFingerprint ], caps)
    let fx = effects doc
    Assert.Equal<string list>([ "Storage"; "Storage"; "Storage"; "Capability" ], fx |> List.map kindOf)
    let subscribe = fx |> List.last
    Assert.Equal("subscribe", subscribe.GetProperty("request").GetProperty("operation").GetString())
    Assert.Equal(0, doc.RootElement.GetProperty("cancellations").GetArrayLength())

[<Fact>]
let ``a contract mismatch is rejected, and later responses never carry a handshake`` () =
    let wrong = (initialize "#/").Replace(fingerprint, "sha256:00")
    let _, doc = send initial wrong
    Assert.Equal("Rejected", doc.RootElement.GetProperty("handshake").GetProperty("kind").GetString())
    let st, _ = boot "#/"
    let _, doc = send st (event "f.title" None (Some "x"))
    Assert.False(doc.RootElement.TryGetProperty("handshake") |> fst)

[<Fact>]
let ``malformed and unknown messages are tolerated and still produce a complete view`` () =
    let st, ok = boot "#/"
    for message in [ "not json"; "{}"; """{"kind":"Mystery"}"""; """{"kind":"Event"}""" ] do
        let _, doc = send st message
        Assert.Equal<Set<string>>(viewKeys ok, viewKeys doc)

// ---------------------------------------------------------------------------
// Routes
// ---------------------------------------------------------------------------

[<Fact>]
let ``routes parse and format round trip, unknown paths and malformed ids are not found`` () =
    let iid = Id.create<InvestigationTag> (Guid("0b0e8d7c-1111-4222-8333-944455556666"))
    for screen in [ HomeScreen; SettingsScreen; WorkspaceScreen iid; ReportScreen iid; MissingScreen "/no/such" ] do
        Assert.Equal(screen, ScreenRoute.parse (ScreenRoute.format screen))
    Assert.Equal(HomeScreen, ScreenRoute.parse "")
    Assert.Equal(WorkspaceScreen iid, ScreenRoute.parse "#/i/0b0e8d7c-1111-4222-8333-944455556666?tab=x")
    Assert.Equal(MissingScreen "/i/not-a-guid", ScreenRoute.parse "#/i/not-a-guid")
    Assert.Equal(MissingScreen "/i/00000000-0000-0000-0000-000000000000", ScreenRoute.parse "#/i/00000000-0000-0000-0000-000000000000")
    Assert.Equal(MissingScreen "/i/0B0E8D7C-1111-4222-8333-944455556666", ScreenRoute.parse "#/i/0B0E8D7C-1111-4222-8333-944455556666")
    for screen in [ HomeScreen; SettingsScreen; WorkspaceScreen iid; ReportScreen iid ] do
        Assert.StartsWith("#/", ScreenRoute.format screen)

[<Fact>]
let ``a deep link to an id not on this device shows not found once loading has finished`` () =
    let st, _ = send initial (initialize "#/i/0b0e8d7c-1111-4222-8333-944455556666")
    let loading = project st |> Map.ofList
    Assert.Equal(JBool true, loading["isLoading"])
    Assert.Equal(JBool false, loading["isMissing"])
    let st, _ = boot "#/i/0b0e8d7c-1111-4222-8333-944455556666"
    let loaded = project st |> Map.ofList
    Assert.Equal(JBool true, loaded["isMissing"])
    Assert.Equal(JBool false, loaded["isLoading"])
    Assert.Equal(JBool false, loaded["isWorkspace"])

// ---------------------------------------------------------------------------
// Projection
// ---------------------------------------------------------------------------

[<Fact>]
let ``every projection carries the same complete set of keys on every screen`` () =
    let st, home = boot "#/"
    let st, ws = start st
    let iid = (currentId st).Value
    let report, _ = send st ("{\"kind\":\"LocationChanged\",\"location\":{\"origin\":\"x\",\"path\":\"/\",\"query\":\"\",\"hash\":\"" + ScreenRoute.format (ReportScreen iid) + "\"}}")
    let _, reportDoc = send report (event "f.title" None (Some ""))
    let _, settings = send st ("{\"kind\":\"LocationChanged\",\"location\":{\"origin\":\"x\",\"path\":\"/\",\"query\":\"\",\"hash\":\"#/settings\"}}")
    let _, missing = send st ("{\"kind\":\"LocationChanged\",\"location\":{\"origin\":\"x\",\"path\":\"/\",\"query\":\"\",\"hash\":\"#/nowhere\"}}")
    let expected = viewKeys ws
    for doc in [ home; reportDoc; settings; missing ] do
        Assert.Equal<Set<string>>(expected, viewKeys doc)
    Assert.True((view ws).GetProperty("isWorkspace").GetBoolean())
    Assert.True((view missing).GetProperty("isMissing").GetBoolean())

[<Fact>]
let ``starting navigates to the workspace, preserves the observation and saves locally`` () =
    let st, _ = boot "#/"
    let st, doc = start st
    let iid = (currentId st).Value
    Assert.Equal("Checkout returns 502 since 02:00.", text doc "wsObservation")
    Assert.Equal("In progress", text doc "wsLifecycle")
    let fx = effects doc
    let nav = fx |> List.find (fun e -> kindOf e = "Navigation")
    Assert.Equal("#/i/" + Id.text iid, nav.GetProperty("url").GetString())
    let keys = fx |> List.filter (fun e -> kindOf e = "Storage") |> List.map (fun e -> e.GetProperty("key").GetString())
    Assert.Contains("rci:inv:" + Id.text iid, keys)
    Assert.Contains("rci:index", keys)
    Assert.Equal("Local only — not synced to GitHub", text doc "remoteStatus")

[<Fact>]
let ``hypothesis actions come from the kernel transition table`` () =
    let st, _ = boot "#/"
    let st, _ = start st
    let st, _ = send st (event "f.hypStatement" None (Some "Expired certificate"))
    let _, doc = send st (event "hyp.add" None None)
    let h = (first ((view doc).GetProperty("hypotheses")))
    for (name, command, _) in Words.hypothesisCommands do
        let legal = Transitions.hypothesis Proposed command |> Option.isSome
        Assert.Equal(not legal, h.GetProperty("hide" + name).GetBoolean())

[<Fact>]
let ``a refused command surfaces its code, rule, message and Aegis fault code`` () =
    let st, _ = boot "#/"
    let st, _ = start st
    let st, _ = send st (event "f.hypStatement" None (Some "Expired certificate"))
    let st, doc = send st (event "hyp.add" None None)
    let hid = (first ((view doc).GetProperty("hypotheses"))).GetProperty("id").GetString()
    let st, _ = send st (event "f.hypReason" None (Some "looks fine"))
    let st, doc = send st (event "hyp.Falsify" (Some hid) None)
    let r = (first ((view doc).GetProperty("refusals")))
    Assert.Equal("HYPOTHESIS.EVIDENCE_REQUIRED", r.GetProperty("code").GetString())
    Assert.Equal("R2.3", r.GetProperty("rule").GetString())
    Assert.Contains("Aegis fault HYPOTHESIS.EVIDENCE_REQUIRED", r.GetProperty("fault").GetString())
    Assert.Equal("Proposed", (first ((view doc).GetProperty("hypotheses"))).GetProperty("state").GetString())
    Assert.Empty(effects doc)
    ignore st

// ---------------------------------------------------------------------------
// Sync truth (R10.6) and token handling (R9.9, R11.3)
// ---------------------------------------------------------------------------

let private token = "ghp_SECRETtoken1234567890"

let private configure (st: AppState) =
    let st, _ = send st (event "f.setOwner" None (Some "kevinmmiller"))
    let st, _ = send st (event "f.setRepo" None (Some "investigations"))
    let st, _ = send st (event "f.setToken" None (Some token))
    send st (event "settings.save" None None)

let private httpEffect (doc: JsonDocument) = effects doc |> List.tryFind (fun e -> kindOf e = "Http")

let private writeResponse = """{"content":{"sha":"abc123","path":"x"},"commit":{"sha":"c1"}}"""

[<Fact>]
let ``remote status is synced only after GitHub confirms the write`` () =
    let st, _ = boot "#/"
    let st, _ = configure st
    let st, doc = start st
    Assert.Contains("not yet", text doc "remoteStatus")
    let read = (httpEffect doc).Value
    Assert.Equal("GET", read.GetProperty("method").GetString())
    // Nothing stored yet: 404, then the write.
    let st, doc = send st (httpSuccess (read.GetProperty("correlationId").GetString()) 404 """{"message":"Not Found"}""")
    let write = (httpEffect doc).Value
    Assert.Equal("PUT", write.GetProperty("method").GetString())
    Assert.Contains("not yet", text doc "remoteStatus")
    // A network failure leaves it pending, never synced.
    let st, doc = send st (httpNetworkFailure (write.GetProperty("correlationId").GetString()))
    Assert.Equal("Changes pending upload to GitHub (not yet synced)", text doc "remoteStatus")
    Assert.Equal(Some Pending, Map.tryFind (currentId st).Value st.Sync)
    // Offline: a new change stays pending and nothing is sent.
    let st, _ = send st (connectivity false)
    let st, _ = send st (event "f.hypStatement" None (Some "Expired certificate"))
    let st, doc = send st (event "hyp.add" None None)
    Assert.True((httpEffect doc).IsNone)
    Assert.StartsWith("Offline — changes pending", text doc "remoteStatus")
    // Back online: resend; only Written makes it synced.
    let st, doc = send st (connectivity true)
    let again = (httpEffect doc).Value
    let st, doc = send st (httpSuccess (again.GetProperty("correlationId").GetString()) 404 """{"message":"Not Found"}""")
    let put = (httpEffect doc).Value
    let st, doc = send st (httpSuccess (put.GetProperty("correlationId").GetString()) 201 writeResponse)
    Assert.StartsWith("Saved to GitHub", text doc "remoteStatus")
    Assert.Equal(Some Synced, Map.tryFind (currentId st).Value st.Sync)

[<Fact>]
let ``a 409 from GitHub is shown as a conflict and nothing is overwritten`` () =
    let st, _ = boot "#/"
    let st, _ = configure st
    let st, doc = start st
    let read = (httpEffect doc).Value
    let st, doc = send st (httpSuccess (read.GetProperty("correlationId").GetString()) 404 "{}")
    let write = (httpEffect doc).Value
    let st, doc = send st (httpSuccess (write.GetProperty("correlationId").GetString()) 409 """{"message":"sha does not match"}""")
    Assert.StartsWith("Conflict with GitHub", text doc "remoteStatus")
    Assert.True((httpEffect doc).IsNone)
    match Map.tryFind (currentId st).Value st.Sync with
    | Some(Conflicted _) -> ()
    | other -> failwithf "expected a conflict, got %A" other

[<Fact>]
let ``the token never appears in any URL, view text or stored investigation`` () =
    let st, _ = boot "#/"
    let st, settingsDoc = configure st
    let st, doc = start st
    let read = (httpEffect doc).Value
    let _, putDoc = send st (httpSuccess (read.GetProperty("correlationId").GetString()) 404 "{}")
    for d in [ settingsDoc; doc; putDoc ] do
        Assert.DoesNotContain(token, (view d).GetRawText())
        for e in effects d do
            match kindOf e with
            | "Http" ->
                Assert.DoesNotContain(token, e.GetProperty("url").GetString())
                Assert.DoesNotContain(token, (if e.TryGetProperty("body") |> fst then e.GetProperty("body").GetString() else ""))
            | "Navigation" -> Assert.DoesNotContain(token, e.GetProperty("url").GetString())
            | "Storage" when e.GetProperty("key").GetString() <> "rci:settings" ->
                Assert.DoesNotContain(token, e.GetRawText())
            | _ -> ()
    // The token travels only in the Authorization header.
    let put = (httpEffect putDoc).Value
    Assert.Equal("Bearer " + token, put.GetProperty("headers").GetProperty("Authorization").GetString())
