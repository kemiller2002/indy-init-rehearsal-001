/// The frozen Causa storage release (causa-data@causa-storage-v1.0.0) as the application reads it, and working-copy
/// file sets as this device stores them. Every check is the contract's own: the vendored Causa.Contract library
/// (src/contract, byte-identical to the release) runs all 17 reader stages, admissibility included, through this
/// engine's History.validate. Requirements: Indy-init requirements/causa/data/v1.0.0 (CONTRACT §3–§12,
/// CLARIFICATIONS CAUSA-CLAR-10, PROVIDER-GITHUB CAUSA-PROV-09/11).
module IndyInitRootCauseInvestigator.Engine.CausaData

open System
open System.Text.Json
open Causa.Contract

/// Identities pinned by the Indy-init requirements (compatibility/release-pin.json). A bundle that does not carry
/// exactly these bytes is not the frozen release and is refused.
[<RequireQualifiedAccess>]
module Pins =
    let tag = "causa-storage-v1.0.0"
    let schemaBundleSha256 = "0a68af8c93190d2e4d6c27beb2f8cb67c3aebdda586d01beb7122b71af179152"
    let datasetIdentitySha256 = "f650a2e509a420872b46177b21f57ef6ebd7625ae4c69dd92198d3ca472c2b86"
    let schemaDir = "contract/v1.0.0/schemas/"
    let fixturesDir = "fixtures/v1.0.0/"
    let goldenDir = "contract/v1.0.0/golden/"

/// One frozen investigation that passed every reader stage and every dataset check.
type DemoInvestigation =
    { ScenarioKey: string
      Loaded: Loaded }

/// One frozen investigation the application refused, with the contract's code (never partially loaded).
type DemoRejection =
    { DirectoryId: string
      ScenarioKey: string
      Code: string
      DomainCode: string option
      Detail: string }

type Dataset =
    { Schemas: SchemaCheck.SchemaSet
      Valid: DemoInvestigation list
      Rejected: DemoRejection list }

let private refuse code detail : Result<'T, CausaError> = CausaError.fail code detail

let private bytesField (files: Map<string, byte[]>) (path: string) : Result<byte[], CausaError> =
    match Map.tryFind path files with
    | Some b -> Ok b
    | None -> refuse Codes.pathInvalid ("the frozen release has no file " + path)

/// The build ships the release as {"tag": ..., "files": {"<release path>": "<base64 bytes>"}} so that exact bytes
/// survive transport (encoding and canonical-form stages need them).
let decodeBundle (text: string) : Result<string * Map<string, byte[]>, CausaError> =
    try
        use doc = JsonDocument.Parse text
        let root = doc.RootElement
        let tag = root.GetProperty("tag").GetString() |> Option.ofObj |> Option.defaultValue ""
        let files =
            root.GetProperty("files").EnumerateObject()
            |> Seq.map (fun p -> p.Name, Convert.FromBase64String(p.Value.GetString() |> Option.ofObj |> Option.defaultValue ""))
            |> Map.ofSeq
        Ok(tag, files)
    with ex -> refuse Codes.jsonMalformed ("the data bundle could not be read: " + ex.Message)

/// CONTRACT §10.3: the bundle manifest pins every schema; its own bytes are the schema identity.
let loadSchemas (files: Map<string, byte[]>) : Result<SchemaCheck.SchemaSet, CausaError> =
    result {
        let! bundleBytes = bytesField files (Pins.schemaDir + "bundle.json")
        do! if Json.sha256Hex bundleBytes = Pins.schemaBundleSha256 then Ok() else refuse Codes.fixtureDrift "schemas/bundle.json is not the pinned schema identity"
        let! bundle = Json.decodeUtf8 bundleBytes |> Result.bind Json.parse |> Result.mapError (CausaError.create Codes.jsonMalformed)
        let entries =
            match bundle with
            | JObj o ->
                match Json.field "schemas" o with
                | Some(JArr xs) -> xs |> List.choose (function JObj e -> (match Json.field "path" e, Json.field "sha256" e with Some(JStr p), Some(JStr h) -> Some(p, h) | _ -> None) | _ -> None)
                | _ -> []
            | _ -> []
        let! documents =
            entries
            |> traverse (fun (path, sha) ->
                bytesField files (Pins.schemaDir + path)
                |> Result.bind (fun b -> if Json.sha256Hex b = sha then Ok b else refuse Codes.fixtureDrift (path + " does not match schemas/bundle.json"))
                |> Result.bind (fun b -> Json.decodeUtf8 b |> Result.bind Json.parse |> Result.mapError (CausaError.create Codes.schemaViolation)))
        let! set = SchemaCheck.load documents |> Result.mapError (fun es -> CausaError.create Codes.schemaViolation (String.Join("; ", es)))
        return!
            match SchemaCheck.unresolvedReferences set with
            | [] -> Ok set
            | es -> refuse Codes.schemaViolation (String.Join("; ", es))
    }

type private Entry =
    { Id: string
      Key: string
      Title: string
      Version: int64
      Lifecycle: string
      StateSha: string
      EventsSha: string
      ManifestSha: string
      SnapshotSha: string }

let private str name o = match Json.field name o with Some(JStr s) -> s | _ -> ""
let private int name o = match Json.field name o with Some(JInt i) -> i | _ -> -1L

let private entriesOf (manifest: JValue) : Entry list =
    match manifest with
    | JObj o ->
        match Json.field "investigations" o with
        | Some(JArr xs) ->
            xs
            |> List.choose (function
                | JObj e ->
                    Some
                        { Id = str "investigationId" e; Key = str "scenarioKey" e; Title = str "title" e; Version = int "expectedVersion" e
                          Lifecycle = str "expectedLifecycle" e; StateSha = str "expectedStateSha256" e; EventsSha = str "eventsSha256" e
                          ManifestSha = str "manifestSha256" e; SnapshotSha = str "snapshotSha256" e }
                | _ -> None)
        | _ -> []
    | _ -> []

/// CAUSA-CLAR-10: every field of the root-manifest entry must describe the investigation exactly as verified, and
/// the golden projection must equal the replayed state.
let private datasetChecks (files: Map<string, byte[]>) (e: Entry) (loaded: Loaded) : Result<unit, CausaError> =
    let lifecycle = match loaded.StateJson with JObj o -> str "lifecycle" o | _ -> ""
    let drift =
        [ "eventsSha256", e.EventsSha = loaded.EventsSha256
          "manifestSha256", e.ManifestSha = loaded.ManifestSha256
          "snapshotSha256", e.SnapshotSha = loaded.SnapshotSha256
          "expectedStateSha256", e.StateSha = loaded.StateSha256
          "expectedVersion", e.Version = loaded.State.Version
          "expectedLifecycle", e.Lifecycle = lifecycle
          "title", e.Title = loaded.State.Title.Value ]
        |> List.filter (snd >> not)
        |> List.map fst
    if not drift.IsEmpty then refuse Codes.fixtureDrift ("the fixture manifest entry disagrees with the investigation: " + String.Join(", ", drift))
    else
        match Map.tryFind (Pins.goldenDir + e.Key + ".state.json") files with
        | None -> refuse Codes.goldenMismatch ("no golden projection for " + e.Key)
        | Some golden ->
            match Json.decodeUtf8 golden |> Result.bind Json.parse |> Result.bind Json.digest with
            | Ok digest when digest = loaded.StateSha256 -> Ok()
            | Ok _ -> refuse Codes.goldenMismatch ("the replayed state differs from golden/" + e.Key + ".state.json")
            | Error detail -> refuse Codes.goldenMismatch detail

/// Load the frozen dataset: dataset identity, then every listed investigation through all reader stages.
/// A failing investigation is reported with its code and is not shown; the others still load (fail closed per item).
let loadDataset (files: Map<string, byte[]>) : Result<Dataset, CausaError> =
    result {
        let! schemas = loadSchemas files
        let! manifestBytes = bytesField files (Pins.fixturesDir + "manifest.json")
        do! if Json.sha256Hex manifestBytes = Pins.datasetIdentitySha256 then Ok() else refuse Codes.fixtureDrift "fixtures/v1.0.0/manifest.json is not the pinned dataset identity"
        let! manifest = Json.decodeUtf8 manifestBytes |> Result.bind Json.parse |> Result.mapError (CausaError.create Codes.jsonMalformed)
        let outcomes =
            entriesOf manifest
            |> List.map (fun e ->
                let dir = Pins.fixturesDir + "investigations/" + e.Id + "/"
                let fileMap =
                    files |> Map.filter (fun path _ -> path.StartsWith(dir, StringComparison.Ordinal)) |> Map.toList |> List.map (fun (p, b) -> p.Substring dir.Length, b) |> Map.ofList
                let verified =
                    Store.ofFileMap e.Id fileMap
                    |> Result.bind (Store.validate schemas)
                    |> Result.bind (fun loaded -> datasetChecks files e loaded |> Result.map (fun () -> loaded))
                match verified with
                | Ok loaded -> Choice1Of2 { ScenarioKey = e.Key; Loaded = loaded }
                | Error err -> Choice2Of2 { DirectoryId = e.Id; ScenarioKey = e.Key; Code = err.Code; DomainCode = err.DomainCode; Detail = err.Detail })
        return
            { Schemas = schemas
              Valid = outcomes |> List.choose (function Choice1Of2 d -> Some d | _ -> None)
              Rejected = outcomes |> List.choose (function Choice2Of2 r -> Some r | _ -> None) }
    }

// ---------------------------------------------------------------------------------------------------------------------
// Working copies on this device: one storage value holds the complete v1 file set, so a save is atomic (CAUSA-CLAR-04
// for a single-value store) and a reader can verify it with the same 17 stages.
// ---------------------------------------------------------------------------------------------------------------------

[<Literal>]
let fileSetFormat = "causa.fileset/1.0.0"

let encodeFileSet (origin: string option) (files: InvestigationFiles) : string =
    let o = Text.Json.Nodes.JsonObject()
    o["format"] <- Text.Json.Nodes.JsonValue.Create fileSetFormat
    o["directoryId"] <- Text.Json.Nodes.JsonValue.Create files.DirectoryId
    origin |> Option.iter (fun s -> o["origin"] <- Text.Json.Nodes.JsonValue.Create s)
    o["manifest"] <- Text.Json.Nodes.JsonValue.Create(Convert.ToBase64String files.Manifest)
    o["events"] <- Text.Json.Nodes.JsonValue.Create(Convert.ToBase64String files.Events)
    o["snapshot"] <- Text.Json.Nodes.JsonValue.Create(Convert.ToBase64String files.Snapshot)
    o.ToJsonString()

/// True when a stored document is a Causa v1 file set (as opposed to the pre-Causa local format).
let isFileSet (text: string) = text.Contains("\"" + fileSetFormat + "\"", StringComparison.Ordinal)

let decodeFileSet (text: string) : Result<InvestigationFiles * string option, CausaError> =
    try
        use doc = JsonDocument.Parse text
        let r = doc.RootElement
        let get (name: string) = r.GetProperty(name).GetString() |> Option.ofObj |> Option.defaultValue ""
        if get "format" <> fileSetFormat then refuse Codes.unsupportedVersion ("unknown stored format " + get "format")
        else
            let origin = match r.TryGetProperty "origin" with | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj | _ -> None
            Ok(
                { DirectoryId = get "directoryId"
                  Manifest = Convert.FromBase64String(get "manifest")
                  Events = Convert.FromBase64String(get "events")
                  Snapshot = Convert.FromBase64String(get "snapshot") },
                origin
            )
    with ex -> refuse Codes.jsonMalformed ("the stored working copy could not be read: " + ex.Message)

/// CONTRACT §2.9: the command boundary supplies millisecond-truncated UTC; finer instants have no v1 wire form.
let truncateToMillisecond (d: DateTimeOffset) = Timestamp.truncateToMillisecond d

/// Describe a contract error for people: the code is always shown, never hidden behind prose.
let describe (e: CausaError) =
    e.Code + (match e.DomainCode with Some d -> " (" + d + ")" | None -> "") + ": " + e.Detail
