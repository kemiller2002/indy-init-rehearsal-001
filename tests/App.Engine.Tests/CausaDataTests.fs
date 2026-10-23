/// The frozen Causa storage release as the application loads it (CAUSA-REHEARSAL-INTEGRATION-001, Phase 2/6).
/// These tests read the vendored bytes in data/causa-storage-v1.0.0, never a reconstructed copy.
module App.Engine.Tests.CausaDataTests

open System
open System.IO
open System.Text
open Xunit
open IndyInitRootCauseInvestigator.Engine
open Causa.Contract

let private releaseRoot = TestData.releaseRoot
let private releaseFiles = TestData.releaseFiles

let private lockStates =
    let lock = File.ReadAllText(Path.Combine(releaseRoot, "compatibility/v1.0.0/lock.json"))
    use doc = Text.Json.JsonDocument.Parse lock
    doc.RootElement.GetProperty("expectedStates").EnumerateArray()
    |> Seq.map (fun e -> e.GetProperty("investigationId").GetString() |> nonNull, e.GetProperty("stateSha256").GetString() |> nonNull)
    |> Map.ofSeq

let private dataset () =
    match CausaData.loadDataset releaseFiles with
    | Ok d -> d
    | Error e -> failwith (CausaData.describe e)

[<Fact>]
let ``the pinned schema identity loads all 60 schemas`` () =
    match CausaData.loadSchemas releaseFiles with
    | Ok _ -> ()
    | Error e -> failwith (CausaData.describe e)

[<Fact>]
let ``all five frozen investigations pass every reader stage and replay to the golden states`` () =
    let d = dataset ()
    Assert.Empty d.Rejected
    Assert.Equal(5, d.Valid.Length)
    for v in d.Valid do
        Assert.Equal(lockStates[Uuid.text v.Loaded.InvestigationId], v.Loaded.StateSha256)

[<Fact>]
let ``the five demonstration titles are the frozen ones`` () =
    let titles = dataset().Valid |> List.map (fun v -> v.Loaded.State.Title.Value) |> List.sort
    let expected =
        [ "Card payments failing at checkout"
          "Error-rate dashboard contradicts application logs"
          "Intermittent 502 errors on the order-status API"
          "Users unable to sign in via single sign-on"
          "p95 latency regression on the search API after release 4.2" ]
        |> List.sort
    Assert.Equal<string list>(expected, titles)

let private corrupt (path: string) (change: string -> string) =
    releaseFiles |> Map.add path (Encoding.UTF8.GetBytes(change (Encoding.UTF8.GetString releaseFiles[path])))

[<Fact>]
let ``a corrupted event stream is refused and only that investigation is withheld`` () =
    let id = "d588748e-8df9-5f27-859e-5d36d9a2b53d"
    let path = "fixtures/v1.0.0/investigations/" + id + "/events.ndjson"
    let files = corrupt path (fun t -> t.Replace("\"aggregateVersion\":3,", "\"aggregateVersion\":4,"))
    match CausaData.loadDataset files with
    | Ok d ->
        Assert.Equal(4, d.Valid.Length)
        let r = Assert.Single d.Rejected
        Assert.Equal(id, r.DirectoryId)
        Assert.Equal("CAUSA.EVENTS_HASH_MISMATCH", r.Code)
    | Error e -> failwith (CausaData.describe e)

[<Fact>]
let ``a dataset that is not the pinned release is refused as a whole`` () =
    let files = corrupt "fixtures/v1.0.0/manifest.json" (fun t -> t.Replace("causa-demo-v1", "causa-demo-v2"))
    match CausaData.loadDataset files with
    | Ok _ -> failwith "an unpinned dataset was accepted"
    | Error e -> Assert.Equal("CAUSA.FIXTURE_DRIFT", e.Code)

[<Fact>]
let ``a working copy round-trips through the device format byte for byte`` () =
    let d = dataset ()
    let v = d.Valid.Head
    match Store.build d.Schemas v.Loaded.Events with
    | Error e -> failwith (CausaData.describe e)
    | Ok(files, _) ->
        let stored = CausaData.encodeFileSet (Some("demo:" + v.ScenarioKey)) files
        match CausaData.decodeFileSet stored with
        | Ok(back, origin) ->
            Assert.Equal(Some("demo:" + v.ScenarioKey), origin)
            Assert.Equal<byte[]>(files.Events, back.Events)
            let original = releaseFiles["fixtures/v1.0.0/investigations/" + Uuid.text v.Loaded.InvestigationId + "/events.ndjson"]
            Assert.Equal<byte[]>(original, back.Events)
        | Error e -> failwith (CausaData.describe e)

[<Fact>]
let ``appending a decided event keeps the frozen history as a byte prefix; a stale version is refused`` () =
    let d = dataset ()
    let v = d.Valid |> List.find (fun x -> x.ScenarioKey = "api-latency")
    let at = DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero)
    let env =
        { CommandId = Guid "9b1c2d6e-0000-4000-8000-0000000000c2"; Investigation = Id.create v.Loaded.InvestigationId; ExpectedVersion = Some v.Loaded.State.Version
          Actor = Actor.human "acceptance"; RequestedAt = at
          Command = RecordObservation(Id.create (Guid "9b1c2d6e-0000-4000-8000-0000000000a2"), "Latency back to normal", None) }
    match Decide.execute (Some v.Loaded.State) env with
    | Error es -> failwith (es |> List.map (fun e -> e.Code) |> String.concat ", ")
    | Ok newEvents ->
        match Store.append d.Schemas v.Loaded v.Loaded.State.Version newEvents with
        | Error e -> failwith (CausaData.describe e)
        | Ok(files, loaded) ->
            let original = releaseFiles["fixtures/v1.0.0/investigations/" + Uuid.text v.Loaded.InvestigationId + "/events.ndjson"]
            Assert.True(files.Events.AsSpan().StartsWith(original.AsSpan()))
            Assert.Equal(v.Loaded.State.Version + 1L, loaded.State.Version)
        match Store.append d.Schemas v.Loaded (v.Loaded.State.Version - 1L) newEvents with
        | Error e -> Assert.Equal("CAUSA.VERSION_STALE", e.Code)
        | Ok _ -> failwith "a stale append was accepted"
