/// The application shell: the Limen wire boundary, routing, the UI state and
/// the projection to a Limen ViewState. Pure: `handle` threads an immutable
/// AppState; entropy (ids) and time arrive in the state via `withContext`, set
/// by the single mutable cell at the WASM export. Legality is never decided
/// here: every command goes through Decide.execute, and every "what can I do
/// now" comes from Transitions and Policy.
module IndyInitRootCauseInvestigator.Engine.Shell

open System
open System.Buffers
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes

// ---------------------------------------------------------------------------
// JSON output (exact) and input (tolerant)
// ---------------------------------------------------------------------------

type J =
    | JStr of string
    | JNum of int64
    | JBool of bool
    | JNull
    | JArr of J list
    | JObj of (string * J) list

[<RequireQualifiedAccess>]
module JsonOut =
    let rec private write (w: Utf8JsonWriter) (j: J) =
        match j with
        | JStr s -> w.WriteStringValue s
        | JNum n -> w.WriteNumberValue n
        | JBool b -> w.WriteBooleanValue b
        | JNull -> w.WriteNullValue()
        | JArr items ->
            w.WriteStartArray()
            items |> List.iter (write w)
            w.WriteEndArray()
        | JObj fields ->
            w.WriteStartObject()
            fields |> List.iter (fun (k, v) -> w.WritePropertyName k; write w v)
            w.WriteEndObject()

    let render (j: J) : string =
        let buffer = ArrayBufferWriter<byte>()
        use w = new Utf8JsonWriter(buffer)
        write w j
        w.Flush()
        Text.Encoding.UTF8.GetString(buffer.WrittenSpan)

[<RequireQualifiedAccess>]
module JsonIn =
    let parse (text: string) : JsonNode option =
        try
            match JsonNode.Parse text with
            | null -> None
            | node -> Some node
        with _ -> None

    let ofNullable (node: JsonNode | null) : JsonNode option =
        match node with
        | null -> None
        | v -> Some v

    let child (name: string) (node: JsonNode option) : JsonNode option =
        match node with
        | Some(:? JsonObject as o) ->
            if o.ContainsKey name then ofNullable o[name] else None
        | _ -> None

    let private kind (node: JsonNode option) =
        match node with
        | Some(:? JsonValue as v) -> Some(v, v.GetValueKind())
        | _ -> None

    let str (node: JsonNode option) : string option =
        match kind node with
        | Some(v, JsonValueKind.String) -> Some(v.GetValue<string>())
        | _ -> None

    let int (node: JsonNode option) : int option =
        match kind node with
        | Some(v, JsonValueKind.Number) ->
            let d = v.GetValue<double>()
            if d = Math.Floor d && abs d < 2147483647.0 then Some(int d) else None
        | _ -> None

    let bool (node: JsonNode option) : bool option =
        match kind node with
        | Some(_, JsonValueKind.True) -> Some true
        | Some(_, JsonValueKind.False) -> Some false
        | _ -> None

    let list (node: JsonNode option) : JsonNode option list =
        match node with
        | Some(:? JsonArray as a) ->
            [ for item in a ->
                  match item with
                  | null -> None
                  | item -> Some item ]
        | _ -> []

    /// Compact text of an arbitrary node (for an Http body delivered as JSON).
    let text (node: JsonNode option) : string option =
        match node with
        | None -> None
        | Some n ->
            match str (Some n) with
            | Some s -> Some s
            | None -> Some(n.ToJsonString())

// ---------------------------------------------------------------------------
// Routes (hash based, relative; SAF-URL)
// ---------------------------------------------------------------------------

type Screen =
    | HomeScreen
    | WorkspaceScreen of InvestigationId
    /// One addressable thing inside an investigation (R9.6): #/i/{id}/{kind}/{entity}.
    | EntityScreen of InvestigationId * EntityKind * Guid
    | ReportScreen of InvestigationId
    /// The frozen original of a demonstration investigation, always read-only: #/demo/{id}.
    | DemoScreen of InvestigationId
    | SettingsScreen
    | FactoryScreen
    | MissingScreen of path: string
    /// A link carrying a reserved parameter (token, code, key, session, ...) is never opened (R9.9).
    | RefusedLinkScreen of parameter: string

[<RequireQualifiedAccess>]
module ScreenRoute =
    /// Path segment for each addressable entity kind.
    let entitySegments =
        [ "h", HypothesisEntity
          "e", EvidenceEntity
          "u", UnknownEntity
          "x", ContradictionEntity
          "t", ActionEntity
          "c", CausalAssertionEntity
          "a", CorrectiveActionEntity
          "k", CriterionEntity
          "p", ProposalEntity ]

    /// Limen's reserved parameter names: never accepted in a link (R9.9).
    let reservedParameters =
        [ "token"; "code"; "key"; "session"; "access_token"; "id_token"; "refresh_token"; "password"; "secret"; "state"; "auth" ]

    let private fragmentOf (location: string) =
        if location.StartsWith("#", StringComparison.Ordinal) then location.Substring 1 else location

    let private pathOf (location: string) =
        let fragment = fragmentOf location
        match fragment.IndexOf '?' with
        | -1 -> fragment
        | index -> fragment.Substring(0, index)

    /// Parameter names in the hash query (values are never read).
    let parameterNames (location: string) : string list =
        let fragment = fragmentOf location
        match fragment.IndexOf '?' with
        | -1 -> []
        | index ->
            fragment.Substring(index + 1).Split('&', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun pair -> (match pair.IndexOf '=' with -1 -> pair | i -> pair.Substring(0, i)).Trim().ToLowerInvariant())
            |> List.ofArray

    let private canonicalId<'Tag> (text: string) : Id<'Tag> option =
        match Id.tryParse<'Tag> text with
        | Some id when Id.text id = text -> Some id
        | _ -> None

    /// Pure and total: a location hash to its screen. Unknown paths and
    /// malformed ids are a value (MissingScreen), never an exception.
    let parse (location: string) : Screen =
        match parameterNames location |> List.tryFind (fun n -> List.contains n reservedParameters) with
        | Some reserved -> RefusedLinkScreen reserved
        | None ->
        let path = pathOf location
        let segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray
        let missing () = MissingScreen(if path.StartsWith("/", StringComparison.Ordinal) then path else "/" + path)
        let exact (parts: string list) = path = "/" + String.Join("/", parts)
        match segments with
        | [] -> HomeScreen
        | [ "settings" ] when exact segments -> SettingsScreen
        | [ "factory" ] when exact segments -> FactoryScreen
        | [ "i"; id ] when exact segments ->
            match canonicalId<InvestigationTag> id with
            | Some iid -> WorkspaceScreen iid
            | None -> missing ()
        | [ "demo"; id ] when exact segments ->
            match canonicalId<InvestigationTag> id with
            | Some iid -> DemoScreen iid
            | None -> missing ()
        | [ "i"; id; "report" ] when exact segments ->
            match canonicalId<InvestigationTag> id with
            | Some iid -> ReportScreen iid
            | None -> missing ()
        | [ "i"; id; segment; entity ] when exact segments ->
            match canonicalId<InvestigationTag> id, List.tryFind (fun (s, _) -> s = segment) entitySegments, canonicalId<InvestigationTag> entity with
            | Some iid, Some(_, kind), Some eid -> EntityScreen(iid, kind, eid.Value)
            | _ -> missing ()
        | _ -> missing ()

    /// Pure: the canonical (relative) location hash of a screen.
    let format (screen: Screen) : string =
        match screen with
        | HomeScreen -> "#/"
        | WorkspaceScreen iid -> "#/i/" + Id.text iid
        | EntityScreen(iid, kind, eid) ->
            let segment = entitySegments |> List.tryFind (fun (_, k) -> k = kind) |> Option.map fst |> Option.defaultValue "h"
            "#/i/" + Id.text iid + "/" + segment + "/" + eid.ToString("D")
        | ReportScreen iid -> "#/i/" + Id.text iid + "/report"
        | DemoScreen iid -> "#/demo/" + Id.text iid
        | SettingsScreen -> "#/settings"
        | FactoryScreen -> "#/factory"
        | MissingScreen path -> "#" + path
        | RefusedLinkScreen _ -> "#/"

    /// The route table published as .echelon/routes.json (name, pattern, path params).
    let table =
        [ "home", "/", []
          "workspace", "/i/{investigation}", [ "investigation" ]
          "report", "/i/{investigation}/report", [ "investigation" ]
          "demo", "/demo/{investigation}", [ "investigation" ]
          "entity", "/i/{investigation}/{kind}/{entity}", [ "investigation"; "kind"; "entity" ]
          "settings", "/settings", []
          "factory", "/factory", []
          "notFound", "/{*rest}", [ "rest" ] ]

// ---------------------------------------------------------------------------
// Limen wire: inbound messages and outbound effects
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module Contract =
    let major = 1
    let minor = 4
    let unit = "limen.core"
    let version = 1
    let fingerprint = "sha256:2d5e16b7111fc78a319706b9927e4523cfcc519b7a2c9352ca8283ba32d6b71c"
    let lifecycleId = "limen.lifecycle"
    let lifecycleVersion = 1
    let lifecycleFingerprint = "sha256:cadd4037cac232869a486237ec523e667af9490cdcf3e4fb1ba729cbc5c87978"

type CapabilityOffer = { CapId: string; CapVersion: int; CapFingerprint: string }

type HandshakeOffer =
    { Major: int
      Minor: int
      ContractUnit: string
      ContractVersion: int
      ContractFingerprint: string
      Offers: CapabilityOffer list }

type HttpOutcome =
    | HttpSuccess of status: int * body: string * headers: Map<string, string>
    | HttpFailure of reason: string * status: int option
    | HttpCancelled
    | HttpOutcomeUnknown of reason: string

type Inbound =
    | Initialize of hash: string * baseUrl: string * handshake: HandshakeOffer option
    | UiEvent of name: string * key: string option * value: string option
    | StorageDone of correlation: string * Result<string option, string>
    | HttpDone of correlation: string * HttpOutcome
    | CapabilityDone of correlation: string * online: bool option
    | NavigationDone of correlation: string
    | ClipboardDone of correlation: string * failure: string option
    | LocationChanged of hash: string
    | ConnectivityChanged of online: bool
    | Unrecognized of reason: string

type Effect =
    | StorageGet of correlation: string * key: string
    | StorageSet of correlation: string * key: string * value: string
    | StorageRemove of correlation: string * key: string
    | HttpCall of correlation: string * httpMethod: string * url: string * headers: (string * string) list * body: string option
    | Navigate of correlation: string * url: string
    | SubscribeConnectivity of correlation: string
    | ClipboardWrite of correlation: string * text: string

type HandshakeReply =
    | NoHandshake
    | Accepted of minor: int * offer: HandshakeOffer * selected: CapabilityOffer list
    | Rejected of J

[<RequireQualifiedAccess>]
module Wire =
    let private hashOf (location: JsonNode option) =
        JsonIn.child "hash" location |> JsonIn.str |> Option.defaultValue ""

    let private offerOf (node: JsonNode option) : HandshakeOffer option =
        match node with
        | None -> None
        | Some _ ->
            let protocol = JsonIn.child "protocol" node
            let contract = JsonIn.child "contract" node
            Some
                { Major = JsonIn.child "major" protocol |> JsonIn.int |> Option.defaultValue 0
                  Minor = JsonIn.child "minor" protocol |> JsonIn.int |> Option.defaultValue 0
                  ContractUnit = JsonIn.child "unit" contract |> JsonIn.str |> Option.defaultValue ""
                  ContractVersion = JsonIn.child "version" contract |> JsonIn.int |> Option.defaultValue 0
                  ContractFingerprint = JsonIn.child "fingerprint" contract |> JsonIn.str |> Option.defaultValue ""
                  Offers =
                    JsonIn.child "capabilities" node
                    |> JsonIn.list
                    |> List.choose (fun c ->
                        match JsonIn.child "id" c |> JsonIn.str, JsonIn.child "version" c |> JsonIn.int, JsonIn.child "fingerprint" c |> JsonIn.str with
                        | Some id, Some v, Some f -> Some { CapId = id; CapVersion = v; CapFingerprint = f }
                        | _ -> None) }

    let private httpOutcome (outcome: JsonNode option) =
        match JsonIn.child "kind" outcome |> JsonIn.str with
        | Some "Success" ->
            let headers =
                match JsonIn.child "headers" outcome with
                | Some(:? JsonObject as o) -> o |> Seq.choose (fun (KeyValue(k, v)) -> JsonIn.ofNullable v |> JsonIn.str |> Option.map (fun x -> k.ToLowerInvariant(), x)) |> Map.ofSeq
                | _ -> Map.empty
            HttpSuccess(JsonIn.child "status" outcome |> JsonIn.int |> Option.defaultValue 0, JsonIn.child "body" outcome |> JsonIn.text |> Option.defaultValue "", headers)
        | Some "Failure" ->
            HttpFailure(JsonIn.child "reason" outcome |> JsonIn.str |> Option.defaultValue "network", JsonIn.child "status" outcome |> JsonIn.int)
        | Some "Cancelled" -> HttpCancelled
        | _ -> HttpOutcomeUnknown(JsonIn.child "reason" outcome |> JsonIn.str |> Option.defaultValue "unknown")

    let private effectResult (result: JsonNode option) : Inbound =
        let correlation = JsonIn.child "correlationId" result |> JsonIn.str |> Option.defaultValue ""
        let outcome = JsonIn.child "outcome" result
        match JsonIn.child "kind" result |> JsonIn.str with
        | Some "StorageResult" ->
            match JsonIn.child "kind" outcome |> JsonIn.str with
            | Some "Success" -> StorageDone(correlation, Ok(JsonIn.child "value" outcome |> JsonIn.str))
            | _ -> StorageDone(correlation, Error(JsonIn.child "reason" outcome |> JsonIn.str |> Option.defaultValue "unavailable"))
        | Some "HttpResult" -> HttpDone(correlation, httpOutcome outcome)
        | Some "NavigationResult" -> NavigationDone correlation
        | Some "CapabilityResult" ->
            let online =
                match JsonIn.child "kind" outcome |> JsonIn.str with
                | Some "Completed" -> JsonIn.child "result" outcome |> JsonIn.child "state" |> JsonIn.child "online" |> JsonIn.bool
                | _ -> None
            CapabilityDone(correlation, online)
        | Some "ClipboardResult" ->
            match JsonIn.child "kind" outcome |> JsonIn.str with
            | Some "Success" -> ClipboardDone(correlation, None)
            | _ -> ClipboardDone(correlation, Some(JsonIn.child "reason" outcome |> JsonIn.str |> Option.defaultValue "unknown"))
        | other -> Unrecognized("effect result " + Option.defaultValue "?" other)

    /// Tolerant decoding: anything outside what the shell understands is a value.
    let decode (json: string) : Inbound =
        let root = JsonIn.parse json
        match JsonIn.child "kind" root |> JsonIn.str with
        | Some "Initialize" ->
            let location = JsonIn.child "location" root
            let origin = JsonIn.child "origin" location |> JsonIn.str |> Option.defaultValue ""
            let path = JsonIn.child "path" location |> JsonIn.str |> Option.defaultValue "/"
            Initialize(hashOf location, origin + path, offerOf (JsonIn.child "handshake" root))
        | Some "Event" ->
            let e = JsonIn.child "event" root
            UiEvent(JsonIn.child "name" e |> JsonIn.str |> Option.defaultValue "", JsonIn.child "key" e |> JsonIn.str, JsonIn.child "value" e |> JsonIn.str)
        | Some "EffectResult" -> effectResult (JsonIn.child "result" root)
        | Some "LocationChanged" -> LocationChanged(hashOf (JsonIn.child "location" root))
        | Some "CapabilityFact" ->
            let fact = JsonIn.child "fact" root
            match JsonIn.child "capability" root |> JsonIn.str, JsonIn.child "kind" fact |> JsonIn.str with
            | Some id, Some "ConnectivityChanged" when id = Contract.lifecycleId ->
                match JsonIn.child "online" fact |> JsonIn.bool with
                | Some online -> ConnectivityChanged online
                | None -> Unrecognized "connectivity fact without online"
            | _ -> Unrecognized "capability fact"
        | Some other -> Unrecognized other
        | None -> Unrecognized "no kind"

    /// The handshake answer (DF-LIMEN-2026-0001): accept protocol major 1 at
    /// the highest common minor, echo the core contract exactly, and select the
    /// lifecycle capability exactly as offered when it is offered.
    let negotiate (offer: HandshakeOffer option) : HandshakeReply =
        match offer with
        | None -> NoHandshake
        | Some o when o.Major <> Contract.major ->
            Rejected(JObj [ "kind", JStr "ProtocolUnsupported"; "offered", JObj [ "major", JNum(int64 o.Major); "minor", JNum(int64 o.Minor) ] ])
        | Some o when o.ContractUnit <> Contract.unit || o.ContractVersion <> Contract.version || o.ContractFingerprint <> Contract.fingerprint ->
            let identity unit (version: int) (fp: string) = JObj [ "unit", JStr unit; "version", JNum(int64 version); "fingerprint", JStr fp ]
            Rejected(
                JObj
                    [ "kind", JStr "ContractMismatch"
                      "expected", identity Contract.unit Contract.version Contract.fingerprint
                      "offered", identity o.ContractUnit o.ContractVersion o.ContractFingerprint ]
            )
        | Some o ->
            let selected =
                o.Offers
                |> List.filter (fun c -> c.CapId = Contract.lifecycleId && c.CapVersion = Contract.lifecycleVersion && c.CapFingerprint = Contract.lifecycleFingerprint)
                |> List.truncate 1
            Accepted(min o.Minor Contract.minor, o, selected)

    let private effectJson (effect: Effect) : J =
        match effect with
        | StorageGet(c, key) -> JObj [ "kind", JStr "Storage"; "operation", JStr "get"; "correlationId", JStr c; "key", JStr key ]
        | StorageSet(c, key, value) -> JObj [ "kind", JStr "Storage"; "operation", JStr "set"; "correlationId", JStr c; "key", JStr key; "value", JStr value ]
        | StorageRemove(c, key) -> JObj [ "kind", JStr "Storage"; "operation", JStr "remove"; "correlationId", JStr c; "key", JStr key ]
        | HttpCall(c, m, url, headers, body) ->
            JObj(
                [ "kind", JStr "Http"; "correlationId", JStr c; "method", JStr m; "url", JStr url ]
                @ (if headers.IsEmpty then [] else [ "headers", JObj(headers |> List.map (fun (k, v) -> k, JStr v)) ])
                @ (match body with Some b -> [ "body", JStr b ] | None -> [])
                @ [ "timeoutMs", JNum 30000L; "response", JStr "text" ]
                @ (if url.StartsWith(CausaGitHub.apiBase + "/", StringComparison.Ordinal) then
                       [ "credentials", JStr "omit"
                         "responseHeaders", JArr [ JStr "retry-after"; JStr "x-ratelimit-remaining"; JStr "x-ratelimit-reset" ] ]
                   else [])
            )
        | ClipboardWrite(c, text) -> JObj [ "kind", JStr "Clipboard"; "correlationId", JStr c; "operation", JStr "writeText"; "text", JStr text ]
        | Navigate(c, url) -> JObj [ "kind", JStr "Navigation"; "operation", JStr "push"; "correlationId", JStr c; "url", JStr url ]
        | SubscribeConnectivity c ->
            JObj
                [ "kind", JStr "Capability"
                  "correlationId", JStr c
                  "capability", JStr Contract.lifecycleId
                  "version", JNum(int64 Contract.lifecycleVersion)
                  "request", JObj [ "operation", JStr "subscribe"; "topics", JArr [ JStr "connectivity" ] ] ]

    let private handshakeJson (reply: HandshakeReply) : (string * J) list =
        match reply with
        | NoHandshake -> []
        | Rejected reason -> [ "handshake", JObj [ "kind", JStr "Rejected"; "reason", reason ] ]
        | Accepted(minor, o, selected) ->
            [ "handshake",
              JObj
                  [ "kind", JStr "Accepted"
                    "protocol", JObj [ "major", JNum(int64 Contract.major); "minor", JNum(int64 minor) ]
                    "contract", JObj [ "unit", JStr o.ContractUnit; "version", JNum(int64 o.ContractVersion); "fingerprint", JStr o.ContractFingerprint ]
                    "capabilities",
                    JArr(selected |> List.map (fun c -> JObj [ "id", JStr c.CapId; "version", JNum(int64 c.CapVersion); "fingerprint", JStr c.CapFingerprint ])) ] ]

    /// Exact output: exactly view, effects, cancellations and (on Initialize) handshake.
    let encode (view: (string * J) list) (effects: Effect list) (reply: HandshakeReply) : string =
        JsonOut.render (JObj([ "view", JObj view; "effects", JArr(effects |> List.map effectJson); "cancellations", JArr [] ] @ handshakeJson reply))

// ---------------------------------------------------------------------------
// Plain-language vocabulary (R16.1: no state-machine jargon on screen)
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module Words =
    let lifecycle (s: InvestigationLifecycle) =
        match s with
        | Draft -> "Draft"
        | Active -> "In progress"
        | AwaitingEvidence -> "Waiting for evidence"
        | AwaitingVerification -> "Waiting for verification"
        | Resolved -> "Resolved"
        | Closed -> "Closed"
        | Archived -> "Archived"

    let hypothesis (s: HypothesisState) =
        match s with
        | Proposed -> "Proposed"
        | Plausible -> "Plausible"
        | Supported -> "Supported by evidence"
        | Weakened -> "Weakened"
        | Falsified -> "Ruled out"
        | Superseded -> "Replaced"

    let hypothesisTone (s: HypothesisState) =
        match s with
        | Supported -> "ok"
        | Proposed | Plausible -> "unknown"
        | Weakened -> "attention"
        | Falsified | Superseded -> "blocked"

    let allHypothesisStates = [ Proposed; Plausible; Supported; Weakened; Falsified; Superseded ]

    /// (event-name suffix, command, button label)
    let hypothesisCommands =
        [ "MarkPlausible", MarkPlausible, "Mark plausible"
          "MarkSupported", MarkSupported, "Mark supported"
          "MarkWeakened", MarkWeakened, "Mark weakened"
          "Falsify", Falsify, "Rule out (falsify)"
          "Supersede", Supersede, "Mark replaced"
          "Reopen", Reopen, "Reopen" ]

    let unknown (s: UnknownState) =
        match s with
        | Identified -> "Open"
        | Investigating -> "Being investigated"
        | UnknownResolved -> "Answered"
        | AcceptedResidual -> "Accepted as remaining uncertainty"
        | UnknownSuperseded -> "Replaced"

    let unknownCommands =
        [ "Start", StartInvestigatingUnknown, "Start investigating"
          "Resolve", ResolveUnknown, "Mark answered"
          "Accept", AcceptUnknownAsResidual, "Accept as remaining uncertainty" ]

    let materiality (m: Materiality) =
        match m with
        | Blocking -> "Blocking"
        | Material -> "Important"
        | NonMaterial -> "Minor"
        | Informational -> "For information"

    let materialities = [ "Blocking", Blocking; "Material", Material; "NonMaterial", NonMaterial; "Informational", Informational ]

    let contradiction (s: ContradictionState) =
        match s with
        | Detected -> "Open"
        | Triaged -> "Reviewed"
        | ContradictionInvestigating -> "Being investigated"
        | ContradictionResolved -> "Explained"
        | ContradictionAcceptedResidual -> "Accepted as remaining uncertainty"
        | ContradictionSuperseded -> "Replaced"

    let contradictionCommands =
        [ "Triage", TriageContradiction, "Mark reviewed"
          "Resolve", ResolveContradiction, "Mark explained"
          "Accept", AcceptContradictionAsResidual, "Accept as remaining uncertainty" ]

    let action (s: ActionState) =
        match s with
        | ActionProposed -> "Proposed"
        | ActionApproved -> "Approved"
        | ActionInProgress -> "In progress"
        | ActionCompleted -> "Done"
        | ActionInterpreted -> "Interpreted"
        | ActionRejected -> "Rejected"
        | ActionCancelled -> "Cancelled"

    let actionCommands =
        [ "Approve", ApproveAction, "Approve"
          "Start", StartAction, "Start"
          "Reject", RejectAction, "Reject"
          "Cancel", CancelAction, "Cancel" ]

    let actionKind (k: ActionKind) =
        match k with
        | Test -> "Test"
        | Inspection -> "Inspection"
        | Query -> "Query"
        | ActionKind.Comparison -> "Comparison"
        | Experiment -> "Experiment"

    let actionKinds = [ "Test", Test; "Inspection", Inspection; "Query", Query; "Comparison", ActionKind.Comparison; "Experiment", Experiment ]

    let cause (s: CausalAssertionState) =
        match s with
        | CauseProposed -> "Proposed"
        | CauseConfirmed -> "Confirmed"
        | CauseReopened -> "Reopened"
        | CauseSuperseded -> "Replaced"

    let role (r: CauseRole) =
        match r with
        | RootCause -> "Root cause"
        | ContributingFactor -> "Contributing factor"
        | Trigger -> "Trigger"
        | EnablingCondition -> "Enabling condition"

    let correction (s: CorrectiveActionState) =
        match s with
        | CorrectionProposed -> "Proposed"
        | CorrectionApproved -> "Approved"
        | CorrectionInProgress -> "Being implemented"
        | Implemented -> "Implemented (not yet verified)"
        | AwaitingVerificationOfEffect -> "Waiting for verification"
        | VerifiedEffective -> "Verified effective"
        | VerificationFailed -> "Verification failed"
        | ReworkRequired -> "Needs rework"
        | CorrectionCancelled -> "Cancelled"

    let correctionCommands =
        [ "Approve", ApproveCorrection, "Approve"
          "Start", StartCorrection, "Start work"
          "Implemented", MarkImplemented, "Mark implemented"
          "Verify", StartVerificationOfEffect, "Start verification"
          "Rework", RequireRework, "Require rework"
          "Cancel", CancelCorrection, "Cancel" ]

    let correctionKind (k: CorrectiveActionKind) =
        match k with
        | Mitigation -> "Temporary mitigation"
        | Workaround -> "Workaround"
        | Correction -> "Root-cause correction"
        | Prevention -> "Prevention"

    let correctionKinds = [ "Correction", Correction; "Mitigation", Mitigation; "Workaround", Workaround; "Prevention", Prevention ]

    let category (c: EvidenceCategory) =
        match c with
        | DirectObservation -> "Direct observation"
        | Measurement -> "Measurement"
        | Log -> "Log"
        | Document -> "Document"
        | ExperimentResult -> "Experiment result"
        | Testimony -> "Testimony"
        | ExternalReference -> "External reference"

    let categories =
        [ "DirectObservation", DirectObservation; "Measurement", Measurement; "Log", Log; "Document", Document
          "ExperimentResult", ExperimentResult; "Testimony", Testimony; "ExternalReference", ExternalReference ]

    let verificationStatus (v: VerificationStatus) =
        match v with
        | Unverified -> "Not checked"
        | PartiallyVerified -> "Partly checked"
        | Verified -> "Checked"
        | Invalidated -> "Shown wrong"

    let relationship (k: RelationshipKind) =
        match k with
        | Supports -> "supports"
        | Contradicts -> "contradicts"
        | Discriminates -> "helps tell apart"
        | Insufficient -> "is not enough to judge"

    let relationships = [ "Supports", Supports; "Contradicts", Contradicts; "Discriminates", Discriminates; "Insufficient", Insufficient ]

    let strength (s: PredictionStrength) =
        match s with
        | Necessary -> "Must happen"
        | Expected -> "Expected"
        | DiscriminatingPrediction -> "Tells hypotheses apart"
        | NonDiscriminating -> "Does not tell apart"

    let strengths = [ "Expected", Expected; "Necessary", Necessary; "DiscriminatingPrediction", DiscriminatingPrediction; "NonDiscriminating", NonDiscriminating ]

    let outcome (o: VerificationOutcome) =
        match o with
        | Satisfied -> "Criterion met"
        | Failed -> "Criterion not met"
        | Inconclusive -> "Inconclusive"

    let outcomes = [ "Satisfied", Satisfied; "Failed", Failed; "Inconclusive", Inconclusive ]

    let conclusion (s: ConclusionStrength) =
        match s with
        | PlausibleExplanation -> "Plausible explanation"
        | LeadingExplanation -> "Leading explanation"
        | SupportedCause -> "Supported cause"
        | ConfirmedCause -> "Confirmed cause"
        | AcceptedWithResidualUncertainty -> "Accepted with remaining uncertainty"

    let conclusions =
        [ "ConfirmedCause", ConfirmedCause; "SupportedCause", SupportedCause; "LeadingExplanation", LeadingExplanation
          "PlausibleExplanation", PlausibleExplanation; "AcceptedWithResidualUncertainty", AcceptedWithResidualUncertainty ]

    let proposal (s: ProposalState) =
        match s with
        | ProposalActive -> "Waiting for your decision"
        | ProposalAccepted -> "Accepted"
        | ProposalRejected -> "Rejected"

    let actor (a: Actor) =
        match a.Kind with
        | Human -> "You"
        | Agent(_, "deterministic-rules") -> "Rule-based helper (not AI)"
        | Agent _ -> "AI suggestion"
        | System -> "System"

    let date (d: DateTimeOffset) = d.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
    let day (d: DateTimeOffset) = d.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)

    let join (sep: string) (items: string list) = String.Join(sep, items)

    let clip (n: int) (s: string) = if s.Length <= n then s else s.Substring(0, n - 1) + "…"

    let states (names: string list) =
        match names with
        | [] -> "never"
        | [ one ] -> one
        | many -> join ", " (List.take (many.Length - 1) many) + " or " + List.last many

/// A guard used by the projection: the lifecycle commands the kernel table knows.
[<RequireQualifiedAccess>]
module LifecycleCommands =
    let all =
        [ "activate", ActivateInvestigation, "Mark in progress"
          "awaitEvidence", MarkAwaitingEvidence "waiting", "Mark waiting for evidence"
          "awaitVerification", MarkAwaitingVerification, "Mark waiting for verification"
          "resolve", ResolveInvestigation, "Resolve investigation"
          "close", CloseInvestigation, "Close"
          "reopen", ReopenInvestigation "reopen", "Reopen investigation" ]

    let legal (state: InvestigationLifecycle) (command: Command) = Transitions.lifecycle state command |> Option.isSome

// ---------------------------------------------------------------------------
// Application state
// ---------------------------------------------------------------------------

/// A refusal as shown to the person: the domain explanation plus its Aegis fault code (R8.2, R8.3).
type Refusal =
    { Code: string
      Message: string
      Rule: string
      FaultCode: string
      Operation: string }

type LocalSave =
    | Saving
    | SavedLocally
    | SaveFailed of reason: string

/// The factory/proof data published with the build (dist/factory.json).
type FactoryRequirement =
    { ReqId: string
      ReqTitle: string
      ReqScope: string
      ReqSlice: string
      /// "tested" | "untested" | "failing" | "" (no test run recorded)
      Coverage: string }

/// Totals of the recorded test run (test-results/traceability.json).
type FactoryTests =
    { RunCommit: string
      Total: int
      Passed: int
      Failed: int
      Tested: int
      Untested: int
      Failing: int
      /// Which suites the recorded run covers (e.g. "engine"); "" when unknown.
      Suites: string }

type FactoryInfo =
    { Label: string
      Commit: string
      BuiltAt: string
      Experiment: string
      Requirements: FactoryRequirement list
      /// id, title, status
      WorkItems: (string * string * string) list
      Tests: FactoryTests option
      /// "passed" | "failing" | "unknown", from the recorded test run.
      BuildStatus: string }

type FactoryState =
    | FactoryNotRequested
    | FactoryLoading
    | FactoryLoaded of FactoryInfo
    | FactoryFailed of reason: string

/// What a finished provider program produced.
type RemoteResult =
    | PublishDone of CausaGitHub.Outcome
    | ResolveDone of CausaGitHub.Outcome
    | RecoverDone of Result<(string * Result<CausaGitHub.Confirmed option, CausaGitHub.Fault>) list, CausaGitHub.Fault>

/// A provider program waiting for its next answer (a continuation, compared by reference).
[<ReferenceEquality>]
type Resume = { Resume: CausaGitHub.Reply -> CausaGitHub.Remote<RemoteResult> }

/// What an outstanding correlation id was for.
type PendingOp =
    | LoadSettings
    | LoadIndex
    | LoadSync
    | LoadInvestigation of InvestigationId
    | SaveInvestigation of InvestigationId
    /// The frozen Causa release bundle (same origin).
    | LoadDataset
    /// Read the device copy before writing: refuse if another window changed it (CONTRACT §9.2/§9.3).
    | CheckBeforeSave of InvestigationId
    /// Write a Causa v1 file set at this aggregate version.
    | SaveFileSet of InvestigationId * version: int64
    /// A separate copy of a version this device would otherwise lose; its result is reported, never assumed.
    | SaveConflictCopy of InvestigationId
    /// Adopting GitHub's verified copy: read the device copy, keep every replaced version as a conflict copy, then replace.
    | AdoptRead of InvestigationId * CausaGitHub.Confirmed * origin: string option
    | AdoptCopy of InvestigationId * CausaGitHub.Confirmed * origin: string option * remaining: string list
    | SaveIndex
    | SaveSettings
    /// The provider record of one investigation (CAUSA-PROV-25), adapter-private.
    | LoadRecord of InvestigationId
    | SaveRecord of InvestigationId
    /// One step of a provider program. The workspace identity travels with it: an answer for another workspace
    /// (settings changed meanwhile) is never adopted (D-009).
    | RemoteStep of InvestigationId option * workspace: string * flight: int64 * Resume
    /// The durable step-7 marker of a publish; the PATCH is sent only once it is stored.
    | KeepMarker of InvestigationId option * workspace: string * flight: int64 * Resume
    | Subscribe
    | NavigationAck
    | CopyLink of url: string
    | FactoryLoad

/// The frozen demonstration dataset (causa-data@causa-storage-v1.0.0), verified on load.
type DemoState =
    | DemoLoading
    | DemoReady of CausaData.Dataset
    | DemoFailed of reason: string

/// How an investigation is stored on this device.
type StoredFormat =
    /// Before Causa storage: the rehearsal's own local codec. Readable, never rewritten or migrated
    /// silently (Causa RULES §6, CONTRACT §2.9), so it is read-only.
    | LegacyFormat
    /// A Causa v1.0.0 file set; `origin` names the frozen demonstration it was copied from, if any.
    | CausaFormat of origin: string option

type AppState =
    { Investigations: Map<InvestigationId, Event list>
      Screen: Screen
      Drafts: Map<string, string>
      Refusals: Refusal list
      Notice: string
      Online: bool
      /// True once the local index and every investigation it names were read.
      Loaded: bool
      Local: Map<InvestigationId, LocalSave>
      Pending: Map<string, PendingOp>
      NextCorrelation: int64
      /// Entropy for new ids, supplied per message at the boundary.
      Entropy: Guid
      IdCounter: int
      Now: DateTimeOffset
      LifecycleNegotiated: bool
      /// origin + path of the page, for absolute share links (never carries a query).
      BaseUrl: string
      Factory: FactoryState
      /// GitHub provider records (CAUSA-PROV-25), by investigation and workspace identity; only the configured
      /// workspace's record counts, the others are kept untouched (CAUSA-PROV-01 §3).
      Records: Map<InvestigationId * string, CausaGitHub.Record>
      /// Stored sync records that could not be read: left untouched, and their investigation is not published.
      RecordUnreadable: Set<InvestigationId>
      /// The provider program in flight, by generation (CAUSA-PROV-20 §7: one per client). Answers for any other
      /// generation are dropped.
      Flight: int64 option
      Generation: int64
      /// GitHub refused the token (401): nothing is sent until it is entered again (CAUSA-PROV-19).
      AuthHalted: bool
      /// Investigations whose publish was tried in this sync cycle without confirmation; retried on the next trigger.
      Attempted: Set<InvestigationId>
      Remote: RemoteConfig option
      /// Index entries as read from this device, kept even when their investigation
      /// could not be decoded, so a save never drops them (R10.7).
      StoredIndex: Map<InvestigationId, IndexCodec.Entry>
      Unreadable: Map<InvestigationId, string>
      /// The saved index could not be read: it is never overwritten.
      IndexUnreadable: bool
      /// A change happened before the initial load finished; the index is written once it has.
      IndexDirty: bool
      Demo: DemoState
      Formats: Map<InvestigationId, StoredFormat>
      /// The aggregate version this window last read from, or wrote to, the device copy.
      StoredVersion: Map<InvestigationId, int64>
      /// Stored file sets read before the schemas were available.
      Deferred: Map<InvestigationId, string>
      /// Local writes refused because another window changed the device copy.
      SaveConflicts: Map<InvestigationId, string> }

let initial: AppState =
    { Investigations = Map.empty
      Screen = HomeScreen
      Drafts = Map.empty
      Refusals = []
      Notice = ""
      Online = true
      Loaded = false
      Local = Map.empty
      Pending = Map.empty
      NextCorrelation = 1L
      Entropy = Guid.Empty
      IdCounter = 0
      Now = DateTimeOffset.UnixEpoch
      LifecycleNegotiated = false
      BaseUrl = ""
      Factory = FactoryNotRequested
      Records = Map.empty
      RecordUnreadable = Set.empty
      Flight = None
      Generation = 0L
      AuthHalted = false
      Attempted = Set.empty
      Remote = None
      StoredIndex = Map.empty
      Unreadable = Map.empty
      IndexUnreadable = false
      IndexDirty = false
      Demo = DemoLoading
      Formats = Map.empty
      StoredVersion = Map.empty
      Deferred = Map.empty
      SaveConflicts = Map.empty }

/// The boundary supplies entropy and the clock for one message; handle stays pure.
/// CONTRACT §2.9: the command boundary supplies millisecond-truncated UTC.
let withContext (entropy: Guid) (now: DateTimeOffset) (state: AppState) : AppState =
    { state with Entropy = entropy; IdCounter = 0; Now = CausaData.truncateToMillisecond now }

[<RequireQualifiedAccess>]
module private Ids =
    let next (st: AppState) : Guid * AppState =
        let n = st.IdCounter + 1
        let counter = BitConverter.GetBytes n
        let bytes = st.Entropy.ToByteArray() |> Array.mapi (fun i b -> if i < 4 then b ^^^ counter[i] else b)
        let g = Guid bytes
        // Guid.Empty is not a valid entity id; perturb deterministically.
        let g = if g = Guid.Empty then Guid("00000000-0000-0000-0000-000000000001") else g
        g, { st with IdCounter = n }

    let correlation (st: AppState) (op: PendingOp) : string * AppState =
        let c = "c" + st.NextCorrelation.ToString(CultureInfo.InvariantCulture)
        c, { st with NextCorrelation = st.NextCorrelation + 1L; Pending = st.Pending |> Map.add c op }

[<RequireQualifiedAccess>]
module Storage =
    let indexKey = "rci:index"
    let settingsKey = "rci:settings"
    let investigationKey (iid: InvestigationId) = "rci:inv:" + Id.text iid
    /// Unique per copy (version, time and correlation), so a later copy never overwrites an earlier one.
    let conflictKey (iid: InvestigationId) (suffix: string) = "causa:conflict:" + Id.text iid + ":" + suffix
// ---------------------------------------------------------------------------
// Reading the current investigation and the drafts
// ---------------------------------------------------------------------------

let private human = Actor.human "local-user"

/// AI proposals are entered by a person on the AI's behalf (no live model).
let private assistant = Actor.agent "ai-assistant" "none" "manual-entry"

/// The "Challenge the leading explanation" helper: fixed rules, no model.
/// Like any agent it can only record proposals (D-004); a person decides.
let private challenger = Actor.agent "challenge-helper" "none" "deterministic-rules"

let investigationOf (st: AppState) (iid: InvestigationId) : Investigation option =
    st.Investigations |> Map.tryFind iid |> Option.bind Evolve.replay

/// The verified frozen demonstration with this id, if the dataset loaded.
let demoOf (st: AppState) (iid: InvestigationId) : CausaData.DemoInvestigation option =
    match st.Demo with
    | DemoReady d -> d.Valid |> List.tryFind (fun v -> v.Loaded.InvestigationId = iid.Value)
    | _ -> None

let schemasOf (st: AppState) =
    match st.Demo with
    | DemoReady d -> Some d.Schemas
    | _ -> None

/// Where the investigation shown on a screen comes from.
type Source =
    | WorkingSource
    | FrozenSource of CausaData.DemoInvestigation

/// A working copy (or any investigation on this device) wins; otherwise the frozen original, read-only.
/// #/demo/{id} always shows the frozen original.
let sourceOf (st: AppState) (iid: InvestigationId) (preferFrozen: bool) : (Investigation * Event list * Source) option =
    let frozen () = demoOf st iid |> Option.map (fun d -> d.Loaded.State, d.Loaded.Events, FrozenSource d)
    if preferFrozen then frozen ()
    else
        match Map.tryFind iid st.Investigations with
        | Some events -> Evolve.replay events |> Option.map (fun inv -> inv, events, WorkingSource)
        | None -> frozen ()

let currentId (st: AppState) : InvestigationId option =
    match st.Screen with
    | WorkspaceScreen iid | ReportScreen iid | EntityScreen(iid, _, _) when (sourceOf st iid false).IsSome -> Some iid
    | DemoScreen iid when (demoOf st iid).IsSome -> Some iid
    | _ -> None

let private draft (st: AppState) (name: string) = st.Drafts |> Map.tryFind name |> Option.defaultValue ""

let private optDraft (st: AppState) (name: string) =
    let v = draft st name
    if String.IsNullOrWhiteSpace v then None else Some v

let private guidDraft (st: AppState) (name: string) : Guid option =
    match Guid.TryParse(draft st name) with
    | true, g when g <> Guid.Empty -> Some g
    | _ -> None

let private choice (table: (string * 'T) list) (fallback: 'T) (value: string) =
    table |> List.tryFind (fun (code, _) -> code = value) |> Option.map snd |> Option.defaultValue fallback

let private dateDraft (st: AppState) (name: string) : DateTimeOffset option =
    match DateTimeOffset.TryParseExact(draft st name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
    | true, d -> Some d
    | _ -> None

let private clear (fields: string list) (st: AppState) =
    { st with Drafts = fields |> List.fold (fun d f -> Map.remove f d) st.Drafts }

/// Every draft field the workspace binds (data-bind-value), so the projection is total.
/// The access token is deliberately absent: it is never projected (R9.9, R11.3).
let draftFields =
    [ "title"; "observation"
      "hypStatement"; "hypMechanism"; "hypReason"; "hypEvidence"
      "aiText"; "aiRationale"; "aiReason"
      "evKind"; "evContent"; "evCategory"; "evSource"; "evCaptured"
      "supOld"; "supNew"; "supReason"
      "relEvidence"; "relHypothesis"; "relKind"; "relRationale"
      "unkQuestion"; "unkMateriality"; "unkBasis"
      "conStatement"; "conEvidence"; "conMateriality"; "conBasis"
      "predHypothesis"; "predStatement"; "predStrength"
      "actKind"; "actPurpose"; "actHypothesis"
      "findAction"; "findResult"; "findInterpretation"
      "negStatement"; "negScope"; "negEvidence"
      "causeOutcome"; "causeRoot"; "causeFactor"; "causeMechanism"
      "corrKind"; "corrDescription"; "corrCause"
      "critCorrection"; "critExpected"; "critMethod"
      "verCriterion"; "verOutcome"; "verEvidence"
      "conclStatement"; "conclStrength"; "conclCause"; "conclResidual"
      "reopenBasis"
      "setOwner"; "setRepo"; "setBranch" ]

// ---------------------------------------------------------------------------
// Persistence and sync plumbing (effects only; results arrive later)
// ---------------------------------------------------------------------------

let private entryOf (iid: InvestigationId) (events: Event list) : IndexCodec.Entry option =
    match Evolve.replay events, List.tryLast events with
    | Some inv, Some last -> Some { Id = iid; Title = inv.Title.Value; Lifecycle = Words.lifecycle inv.Lifecycle; Version = inv.Version; UpdatedAt = last.At }
    | _ -> None

let private storageSet (op: PendingOp) (key: string) (value: string) (st: AppState) : AppState * Effect list =
    let c, st = Ids.correlation st op
    st, [ StorageSet(c, key, value) ]

/// Which workspace (repository, branch and run) provider records refer to.
let remoteIdentity (config: RemoteConfig) =
    CausaGitHub.Workspace.identity { Owner = config.Owner; Repo = config.Repository; Branch = config.Branch; RunId = config.RunId }

let private currentIdentity (st: AppState) = st.Remote |> Option.map remoteIdentity |> Option.defaultValue ""

/// "rci:settings" holds the workspace only. The access token is never
/// persisted: it lives in engine memory for this session (R11.3, CAUSA-PROV-36).
let settingsDocument (config: RemoteConfig) : string =
    JsonOut.render (JObj [ "owner", JStr config.Owner; "repository", JStr config.Repository; "branch", JStr config.Branch; "runId", JStr config.RunId ])

let private readSettings (text: string) : RemoteConfig option =
    let root = JsonIn.parse text
    let field name = JsonIn.child name root |> JsonIn.str
    match field "owner", field "repository" with
    | Some owner, Some repo when owner <> "" && repo <> "" ->
        // A token stored by an older build is ignored, never adopted.
        Some { Owner = owner; Repository = repo; Branch = field "branch" |> Option.defaultValue "main"; RunId = field "runId" |> Option.defaultValue ""; Token = "" }
    | _ -> None

let private hasToken (st: AppState) = st.Remote |> Option.exists (fun r -> r.Token <> "")

/// After every successful command: save locally (investigation + index), then mark
/// the remote copy pending and try to send it (R10.5, R10.6).
/// The index to store: what was read from this device (including entries whose
/// investigation could not be decoded) merged with what is in memory. Written
/// only after the initial load, and never over an unreadable index (R10.7).
let private saveIndex (st: AppState) : AppState * Effect list =
    if st.IndexUnreadable then st, []
    elif not st.Loaded then { st with IndexDirty = true }, []
    else
        let entries =
            st.Investigations
            |> Map.fold (fun m i evs -> match entryOf i evs with Some e -> Map.add i e m | None -> m) st.StoredIndex
        let st = { st with StoredIndex = entries; IndexDirty = false }
        storageSet SaveIndex Storage.indexKey (IndexCodec.encode (entries |> Map.toList |> List.map snd)) st

let private formatOf (st: AppState) (iid: InvestigationId) =
    Map.tryFind iid st.Formats |> Option.defaultValue (CausaFormat None)

/// The Causa v1 file set of an investigation as this window holds it, built and re-verified by the contract
/// library exactly as a reader would (CONTRACT §9.2): a writer never emits what a reader rejects.
let private fileSetOf (st: AppState) (iid: InvestigationId) : Result<Causa.Contract.InvestigationFiles * Causa.Contract.Loaded, Causa.Contract.CausaError> =
    match schemasOf st, Map.tryFind iid st.Investigations with
    | Some schemas, Some events -> Causa.Contract.Store.build schemas events
    | None, _ -> Causa.Contract.CausaError.fail Causa.Contract.Codes.schemaViolation "the Causa storage schemas are not loaded, so nothing can be verified or saved"
    | _, None -> Causa.Contract.CausaError.fail Causa.Contract.Codes.pathInvalid "no such investigation on this device"


// ---------------------------------------------------------------------------
// GitHub provider plumbing (CAUSA-PROV-13…26): a provider program runs one
// step per effect; its continuation waits in Pending until the answer arrives.
// ---------------------------------------------------------------------------

let private stamp (st: AppState) = st.Now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)

let private workspaceOf (cfg: RemoteConfig) : CausaGitHub.Workspace =
    { Owner = cfg.Owner; Repo = cfg.Repository; Branch = cfg.Branch; RunId = cfg.RunId }

/// The provider record of an investigation for the configured workspace; a record for another workspace does not count.
let private recordOf (st: AppState) (iid: InvestigationId) : CausaGitHub.Record option =
    st.Remote |> Option.bind (fun cfg -> Map.tryFind (iid, remoteIdentity cfg) st.Records)

let private withRecord (iid: InvestigationId) (r: CausaGitHub.Record) (st: AppState) =
    { st with Records = st.Records |> Map.add (iid, CausaGitHub.Workspace.identity r.Workspace) r }

/// Every workspace's record of one investigation, as stored together under its key.
let private bookOf (st: AppState) (iid: InvestigationId) =
    st.Records |> Map.toList |> List.filter (fun ((i, _), _) -> i = iid) |> List.map snd

/// CAUSA-PROV-31 §4: text that leaves the adapter never carries the token.
let private redact (st: AppState) (text: string) =
    match st.Remote with
    | Some cfg when cfg.Token <> "" -> text.Replace(cfg.Token, "[REDACTED]")
    | _ -> text

let private saveRecord (iid: InvestigationId) (r: CausaGitHub.Record) (st: AppState) : AppState * Effect list =
    if st.RecordUnreadable.Contains iid then st, [] // never written over a stored record that could not be read
    else
        let st = withRecord iid r st
        storageSet (SaveRecord iid) (CausaGitHub.Record.key (Id.text iid)) (CausaGitHub.Record.encodeBook (bookOf st iid)) st

/// CAUSA-PROV-07 §1: every remote copy passes all 17 reader stages before it is used.
let private verifier (st: AppState) (directoryId: string) (files: Causa.Contract.InvestigationFiles) : Result<unit, Causa.Contract.CausaError> =
    match schemasOf st with
    | None -> Causa.Contract.CausaError.fail Causa.Contract.Codes.schemaViolation "the Causa storage schemas are not loaded"
    | Some _ when files.DirectoryId <> directoryId -> Causa.Contract.CausaError.fail Causa.Contract.Codes.pathInvalid "the files belong to another directory"
    | Some schemas -> Causa.Contract.Store.validate schemas files |> Result.map ignore

let private sessionOf (cfg: RemoteConfig) (st: AppState) : CausaGitHub.Session =
    { Workspace = workspaceOf cfg
      Token = cfg.Token
      ApiBase = CausaGitHub.apiBase
      Nonce = st.Entropy.ToString("N").Substring(0, 12) + "-" + string st.NextCorrelation }

let private sameFiles (a: Causa.Contract.InvestigationFiles) (b: Causa.Contract.InvestigationFiles) =
    a.Events = b.Events && a.Manifest = b.Manifest && a.Snapshot = b.Snapshot

/// The frozen original's exact files, published first for a working copy (CAUSA-PROV-11).
/// Its bytes must hash to the digests the verified root manifest recorded (CAUSA-PROV-11 §2), or nothing is published.
let private frozenFilesOf (st: AppState) (iid: InvestigationId) =
    let digest (b: byte[]) = Causa.Contract.Json.sha256Hex b
    // manifest.json and snapshot.json are hashed without their final LF (CONTRACT §10).
    let withoutLf (b: byte[]) = if b.Length > 0 && b[b.Length - 1] = 10uy then b[.. b.Length - 2] else b
    match demoOf st iid, schemasOf st, formatOf st iid with
    | Some d, Some schemas, CausaFormat(Some _) ->
        Causa.Contract.Store.build schemas d.Loaded.Events
        |> Result.toOption
        |> Option.map fst
        |> Option.filter (fun f ->
            digest f.Events = d.Loaded.EventsSha256
            && digest (withoutLf f.Manifest) = d.Loaded.ManifestSha256
            && digest (withoutLf f.Snapshot) = d.Loaded.SnapshotSha256)
    | _ -> None

/// The state shown: a confirmed record whose investigation changed since is pending again (CAUSA-PROV-22 §3).
let private shownState (st: AppState) (iid: InvestigationId) (r: CausaGitHub.Record) =
    match r.Status, r.Confirmed, fileSetOf st iid with
    | CausaGitHub.SyncConfirmed, Some c, Ok(files, _) when not (sameFiles c.Files files) -> CausaGitHub.SyncPending
    | CausaGitHub.SyncConfirmed, None, _ -> CausaGitHub.SyncPending
    | state, _, _ -> state

type private Work =
    | PublishFiles of Causa.Contract.InvestigationFiles * workingCopyOf: string option
    | ResolveFiles of Causa.Contract.InvestigationFiles

let private workFor (st: AppState) (iid: InvestigationId) (r: CausaGitHub.Record) : Work option =
    match r.Status with
    | CausaGitHub.SyncUnknown -> r.InFlight |> Option.map ResolveFiles
    | CausaGitHub.SyncConflicted | CausaGitHub.SyncRefused | CausaGitHub.SyncFailed -> None
    | CausaGitHub.SyncPending | CausaGitHub.SyncSending | CausaGitHub.SyncConfirmed ->
        match fileSetOf st iid with
        | Error _ -> None
        | Ok(files, _) ->
            match r.Confirmed, frozenFilesOf st iid with
            | Some c, _ when sameFiles c.Files files -> None
            | None, Some frozen when not (sameFiles frozen files) -> Some(PublishFiles(frozen, Some "causa-demo-v1"))
            | None, Some _ -> Some(PublishFiles(files, Some "causa-demo-v1"))
            | _ -> Some(PublishFiles(files, None))

let private lineCount (bytes: byte[]) = bytes |> Array.sumBy (fun b -> if b = 10uy then 1 else 0)

let private attemptOf (st: AppState) (session: CausaGitHub.Session) (iid: InvestigationId) (r: CausaGitHub.Record) (files: Causa.Contract.InvestigationFiles) (message: string) : CausaGitHub.Attempt =
    { Session = session
      InvestigationId = Id.text iid
      Base = r.Confirmed
      Files = files
      Message = message
      Now = stamp st
      Verify = verifier st (Id.text iid)
      Record =
        fun marker ->
            let marked = { r with Status = CausaGitHub.SyncSending; InFlight = Some files; Publish = Some marker }
            let others = bookOf st iid |> List.filter (fun x -> CausaGitHub.Workspace.identity x.Workspace <> CausaGitHub.Workspace.identity r.Workspace)
            CausaGitHub.Record.key (Id.text iid), CausaGitHub.Record.encodeBook (others @ [ marked ]) }

let private applyOutcome (iid: InvestigationId) (outcome: CausaGitHub.Outcome) (st: AppState) : AppState * Effect list =
    match recordOf st iid with
    | None -> st, []
    | Some r ->
        let at = stamp st
        let say = redact st
        let move state cause detail (x: CausaGitHub.Record) = CausaGitHub.Record.transition state at cause (say detail) x
        let r =
            match outcome with
            | CausaGitHub.Outcome.Confirmed c ->
                { r with Confirmed = Some c; InFlight = None; Publish = None; Remote = None; LastError = None }
                |> move CausaGitHub.SyncConfirmed "confirmed" ("commit " + c.Commit.Substring(0, min 7 c.Commit.Length) + " re-read by SHA")
            | CausaGitHub.Outcome.BackToPending f ->
                { r with InFlight = None; Publish = None; LastError = Some(say (CausaGitHub.Fault.describe f)) } |> move CausaGitHub.SyncPending f.Cause f.Detail
            | CausaGitHub.Outcome.Failed f ->
                { r with InFlight = None; Publish = None; LastError = Some(say (CausaGitHub.Fault.describe f)) } |> move CausaGitHub.SyncFailed f.Cause f.Detail
            | CausaGitHub.Outcome.Unknown(cause, detail) -> { r with LastError = Some(say detail) } |> move CausaGitHub.SyncUnknown cause detail
            | CausaGitHub.Outcome.Conflicted(cause, detail, remote) ->
                { r with InFlight = None; Publish = None; Remote = remote; LastError = Some(say (cause + ": " + detail)) } |> move CausaGitHub.SyncConflicted cause detail
            | CausaGitHub.Outcome.Refused(cause, detail) ->
                { r with InFlight = None; Publish = None; LastError = Some(say (cause + ": " + detail)) } |> move CausaGitHub.SyncRefused cause detail
        saveRecord iid r st

/// The record of an investigation for the configured workspace, or a fresh one.
let private recordOrNew (st: AppState) (iid: InvestigationId) =
    match st.Remote, recordOf st iid with
    | _, Some r -> Some r
    | Some cfg, None -> Some(CausaGitHub.Record.create (workspaceOf cfg) (Id.text iid))
    | None, None -> None

/// Make GitHub's verified copy the investigation on this device (the person chose it, or it was recovered).
/// First the device copy is read; every version this would replace (the device's and this window's) is written as
/// its own conflict copy; only when every copy is stored is the investigation replaced (nothing lost silently).
let private adoptConfirmed (iid: InvestigationId) (c: CausaGitHub.Confirmed) (origin: string option) (st: AppState) : AppState * Effect list =
    match schemasOf st |> Option.map (fun schemas -> Causa.Contract.Store.validate schemas c.Files) with
    | Some(Ok _) ->
        let r, st = Ids.correlation st (AdoptRead(iid, c, origin))
        st, [ StorageGet(r, Storage.investigationKey iid) ]
    | Some(Error e) -> { st with Notice = "GitHub's copy could not be verified and was not used: " + CausaData.describe e }, []
    | None -> st, []

let private finishAdopt (iid: InvestigationId) (c: CausaGitHub.Confirmed) (origin: string option) (st: AppState) : AppState * Effect list =
    match schemasOf st |> Option.map (fun schemas -> Causa.Contract.Store.validate schemas c.Files) with
    | Some(Ok loaded) ->
        let st =
            { st with
                Investigations = st.Investigations |> Map.add iid loaded.Events
                Formats = st.Formats |> Map.add iid (CausaFormat origin)
                Local = st.Local |> Map.add iid Saving
                Unreadable = st.Unreadable |> Map.remove iid
                SaveConflicts = st.SaveConflicts |> Map.remove iid }
        let c1, st = Ids.correlation st (SaveFileSet(iid, loaded.State.Version))
        let st, saveR =
            match recordOrNew st iid with
            | Some r ->
                let r =
                    { r with Confirmed = Some c; Remote = None; InFlight = None; Publish = None; LastError = None }
                    |> CausaGitHub.Record.transition CausaGitHub.SyncConfirmed (stamp st) "adopted" ("GitHub's copy at commit " + c.Commit.Substring(0, min 7 c.Commit.Length) + " was adopted")
                saveRecord iid r st
            | None -> st, []
        let st, idx = saveIndex st
        { st with Notice = (if st.Notice.StartsWith("Using GitHub's version", StringComparison.Ordinal) then "GitHub's version is now the investigation on this device; the version it replaced is kept as a separate conflict copy." else st.Notice) },
        [ StorageSet(c1, Storage.investigationKey iid, CausaData.encodeFileSet origin c.Files) ] @ saveR @ idx
    | _ -> { st with Notice = "GitHub's copy could not be verified and was not used." }, []

/// Write the next conflict copy of an adoption, or finish it once all are stored.
let private adoptCopies (iid: InvestigationId) (c: CausaGitHub.Confirmed) (origin: string option) (copies: string list) (st: AppState) : AppState * Effect list =
    match copies with
    | [] -> finishAdopt iid c origin st
    | text :: rest ->
        let key = Storage.conflictKey iid (stamp st + "-" + string st.NextCorrelation)
        let w, st = Ids.correlation st (AdoptCopy(iid, c, origin, rest))
        st, [ StorageSet(w, key, text) ]

/// The device copy was read: keep it, and this window's version, unless either is what is being adopted.
let private onAdoptRead (iid: InvestigationId) (c: CausaGitHub.Confirmed) (origin: string option) (stored: string option) (st: AppState) : AppState * Effect list =
    let adopted = CausaData.encodeFileSet origin c.Files
    let isAdopted (text: string) =
        match CausaData.decodeFileSet text with
        | Ok(f, _) -> sameFiles f c.Files
        | Error _ -> false
    let memory = fileSetOf st iid |> Result.toOption |> Option.map (fun (f, _) -> CausaData.encodeFileSet (match formatOf st iid with CausaFormat o -> o | LegacyFormat -> None) f)
    let copies =
        [ stored; memory ]
        |> List.choose id
        |> List.filter (fun t -> t <> adopted && not (isAdopted t))
        |> List.distinct
    adoptCopies iid c origin copies st

/// The frozen original as recorded, if this history really starts with it (byte prefix), else no origin.
let private originOf (st: AppState) (iid: InvestigationId) (files: Causa.Contract.InvestigationFiles) =
    match demoOf st iid, schemasOf st with
    | Some d, Some schemas ->
        Causa.Contract.Store.build schemas d.Loaded.Events
        |> Result.toOption
        |> Option.filter (fun (frozen, _) -> files.Events.AsSpan().StartsWith(frozen.Events.AsSpan()))
        |> Option.map (fun _ -> CausaData.Pins.tag + "/" + d.ScenarioKey)
    | _ -> None

/// Investigations this device already holds in any form: loaded, waiting for the schemas, or unreadable.
/// Recovery never writes over any of them.
let private heldOnDevice (st: AppState) =
    Set.unionMany
        [ st.Investigations |> Map.toList |> List.map fst |> Set.ofList
          st.Unreadable |> Map.toList |> List.map fst |> Set.ofList
          st.Deferred |> Map.toList |> List.map fst |> Set.ofList
          st.StoredIndex |> Map.toList |> List.map fst |> Set.ofList ]

let private recovered (result: Result<(string * Result<CausaGitHub.Confirmed option, CausaGitHub.Fault>) list, CausaGitHub.Fault>) (st: AppState) : AppState * Effect list =
    match result with
    | Error f -> { st with Notice = "The run on GitHub could not be listed: " + redact st (CausaGitHub.Fault.describe f) }, []
    | Ok items ->
        let held = heldOnDevice st
        let st, fx, count, problems =
            items
            |> List.fold
                (fun (s, fx, n, problems) (name, r) ->
                    match Id.tryParse<InvestigationTag> name, r with
                    | Some iid, Ok(Some c) when not (held.Contains iid) ->
                        let s, more = adoptConfirmed iid c (originOf s iid c.Files) s
                        s, fx @ more, n + 1, problems
                    | _, Ok _ -> s, fx, n, problems
                    | _, Error f -> s, fx, n, problems @ [ name + " (" + f.Cause + ")" ])
                (st, [], 0, [])
        let notice =
            string count + " investigation(s) recovered from GitHub run " + (st.Remote |> Option.map (fun r -> r.RunId) |> Option.defaultValue "")
            + (if problems.IsEmpty then "." else "; not loaded: " + String.Join(", ", problems) + ".")
        { st with Notice = notice }, fx

let private recoverProgram (st: AppState) (session: CausaGitHub.Session) : CausaGitHub.Remote<RemoteResult> =
    let have = heldOnDevice st |> Set.map Id.text
    CausaGitHub.list session
    |> CausaGitHub.Remote.bind (fun listed ->
        match listed with
        | Error f -> CausaGitHub.Done(RecoverDone(Error f))
        | Ok entries ->
            let invalid =
                entries
                |> List.choose (fun (name, ok) ->
                    match ok with
                    | Error code -> Some(name, Error({ Kind = CausaGitHub.PathInvalid; Phase = "R"; Status = None; Cause = code; Detail = name + " is not an investigation directory" }: CausaGitHub.Fault))
                    | Ok() -> None)
            let wanted = entries |> List.filter (fun (name, ok) -> ok = Ok() && not (have.Contains name)) |> List.map fst
            let rec loadAll acc ids =
                match ids with
                | [] -> CausaGitHub.Done(RecoverDone(Ok(invalid @ List.rev acc)))
                | id :: rest -> CausaGitHub.load session id (verifier st id) |> CausaGitHub.Remote.bind (fun r -> loadAll ((id, r) :: acc) rest)
            loadAll [] wanted)

let private replyOf (outcome: HttpOutcome) : CausaGitHub.Reply =
    match outcome with
    | HttpSuccess(status, body, headers) -> CausaGitHub.Answered(status, body, headers)
    | HttpFailure(_, Some status) -> CausaGitHub.Answered(status, "", Map.empty)
    | HttpFailure(reason, None) -> CausaGitHub.NotSent reason
    | HttpCancelled -> CausaGitHub.NotSent "cancelled"
    | HttpOutcomeUnknown reason -> CausaGitHub.Unanswered reason

/// The workspace changed while a program was in flight: its later answers are dropped (by generation); a publish that
/// may have reached the PATCH is unknown, never confirmed (T19/T20 applied in memory).
let private abandon (st: AppState) =
    { st with Flight = None; Records = st.Records |> Map.map (fun _ r -> CausaGitHub.Record.recover (stamp st) r) }

let rec private advance (iid: InvestigationId option) (identity: string) (flight: int64) (program: CausaGitHub.Remote<RemoteResult>) (st: AppState) : AppState * Effect list =
    match program with
    | CausaGitHub.Done result -> finish iid result { st with Flight = None }
    | CausaGitHub.Next(CausaGitHub.Call request, k) ->
        let c, st = Ids.correlation st (RemoteStep(iid, identity, flight, { Resume = k }))
        { st with Flight = Some flight }, [ HttpCall(c, request.Method, request.Url, request.Headers, request.Body) ]
    | CausaGitHub.Next(CausaGitHub.Keep(key, value), k) ->
        let c, st = Ids.correlation st (KeepMarker(iid, identity, flight, { Resume = k }))
        let st =
            match CausaGitHub.Record.decodeBook value with
            | Ok records -> records |> List.fold (fun s r -> withRecord (Id.create (Guid r.InvestigationId)) r s) st
            | Error _ -> st
        { st with Flight = Some flight }, [ StorageSet(c, key, value) ]

and private finish (iid: InvestigationId option) (result: RemoteResult) (st: AppState) : AppState * Effect list =
    let halt (outcome: CausaGitHub.Outcome) (s: AppState) =
        match outcome with
        | CausaGitHub.Outcome.Failed f when f.Kind = CausaGitHub.Authentication ->
            { s with AuthHalted = true; Notice = "GitHub refused the access token. Nothing more is sent until it is entered again in GitHub settings." }
        | _ -> s
    match result, iid with
    | PublishDone outcome, Some i ->
        let st, fx = applyOutcome i outcome st
        let st = halt outcome st
        let st, more =
            match outcome with
            // An ambiguous PATCH is resolved at once, by SHA reads only (CAUSA-PROV-24).
            | CausaGitHub.Outcome.Unknown _ -> startResolve i st
            | CausaGitHub.Outcome.Confirmed _ -> syncNext st
            | _ -> syncNext { st with Attempted = st.Attempted |> Set.add i }
        st, fx @ more
    | ResolveDone outcome, Some i ->
        let st, fx = applyOutcome i outcome st
        let st = halt outcome st
        let st = match outcome with CausaGitHub.Outcome.Confirmed _ -> st | _ -> { st with Attempted = st.Attempted |> Set.add i }
        let st, more = syncNext st
        st, fx @ more
    | RecoverDone r, _ ->
        let st, fx = recovered r st
        let st, more = syncNext st
        st, fx @ more
    | _ -> st, []

/// One provider program at a time: unknown outcomes are resolved before anything pending is sent (CAUSA-PROV-24 §5).
/// Not published: earlier-format copies, investigations whose stored sync record is unreadable, and those whose
/// local save was refused because another window changed them (the device and this window disagree).
and private syncNext (st: AppState) : AppState * Effect list =
    match st.Remote, schemasOf st with
    | Some cfg, Some _ when st.Flight.IsNone && not st.AuthHalted && st.Loaded && st.Online && cfg.Token <> "" && Result.isOk (CausaGitHub.Workspace.check (workspaceOf cfg)) ->
        let candidates =
            st.Investigations
            |> Map.toList
            |> List.map fst
            |> List.filter (fun i ->
                formatOf st i <> LegacyFormat && not (st.Attempted.Contains i) && not (st.RecordUnreadable.Contains i) && not (st.SaveConflicts.ContainsKey i))
            |> List.choose (fun i ->
                let r = recordOf st i |> Option.defaultValue (CausaGitHub.Record.create (workspaceOf cfg) (Id.text i))
                workFor st i r |> Option.map (fun w -> i, r, w))
            |> List.sortBy (fun (_, _, w) -> match w with ResolveFiles _ -> 0 | PublishFiles _ -> 1)
        match candidates with
        | [] -> st, []
        | (i, r, work) :: _ -> begin' cfg i r work st
    | _ -> st, []

and private startResolve (iid: InvestigationId) (st: AppState) : AppState * Effect list =
    match st.Remote, recordOf st iid with
    | Some cfg, Some r when r.Status = CausaGitHub.SyncUnknown && r.InFlight.IsSome && cfg.Token <> "" && st.Online && not st.AuthHalted ->
        begin' cfg iid r (ResolveFiles r.InFlight.Value) st
    | _ -> syncNext { st with Attempted = st.Attempted |> Set.add iid }

and private begin' (cfg: RemoteConfig) (iid: InvestigationId) (r: CausaGitHub.Record) (work: Work) (st: AppState) : AppState * Effect list =
    let session = sessionOf cfg st
    let identity = remoteIdentity cfg
    let flight = st.Generation + 1L
    let st = { st with Generation = flight; Flight = Some flight }
    match work with
    | ResolveFiles files -> advance (Some iid) identity flight (CausaGitHub.resolve (attemptOf st session iid r files "") |> CausaGitHub.Remote.map ResolveDone) st
    | PublishFiles(files, copyOf) ->
        let from = r.Confirmed |> Option.map (fun c -> lineCount c.Files.Events) |> Option.defaultValue 0
        let upTo = lineCount files.Events
        let message = CausaGitHub.commitMessage session.Workspace (Id.text iid) (int64 from) (int64 upTo) (upTo - from) copyOf
        let r =
            { r with InFlight = Some files; Publish = None; LastError = None }
            |> CausaGitHub.Record.transition CausaGitHub.SyncSending (stamp st) "publish" ("publishing version " + string upTo)
        let st, save = saveRecord iid r st
        let st, fx = advance (Some iid) identity flight (CausaGitHub.publish (attemptOf st session iid r files message) |> CausaGitHub.Remote.map PublishDone) st
        st, save @ fx

/// Start the recovery program (one program at a time).
let private startRecover (cfg: RemoteConfig) (st: AppState) : AppState * Effect list =
    let flight = st.Generation + 1L
    let st = { st with Generation = flight; Flight = Some flight }
    advance None (remoteIdentity cfg) flight (recoverProgram st (sessionOf cfg st)) st

let private persistAfterChange (iid: InvestigationId) (st: AppState) : AppState * Effect list =
    match Map.tryFind iid st.Investigations with
    | None -> st, []
    | Some _ ->
        let st = { st with Local = st.Local |> Map.add iid Saving }
        // Read the device copy first; the write happens only if no other window changed it meanwhile.
        let c, st = Ids.correlation st (CheckBeforeSave iid)
        let saveInv = [ StorageGet(c, Storage.investigationKey iid) ]
        let st, saveIndex = saveIndex st
        // A change is a sync trigger (CAUSA-PROV-20 §5); a program in flight is followed by the next once it ends.
        let st, sync = syncNext { st with Attempted = st.Attempted |> Set.remove iid }
        st, saveInv @ saveIndex @ sync

let private saveSettings (config: RemoteConfig option) (st: AppState) : AppState * Effect list =
    // Records stay bound to the workspace they were made for (CAUSA-PROV-01 §3); nothing is moved or deleted.
    // A program in flight keeps running when only the token changes; it is abandoned when the workspace changes.
    let identity = config |> Option.map remoteIdentity |> Option.defaultValue ""
    let st = if st.Flight.IsSome && identity <> currentIdentity st then abandon st else st
    let newToken = config |> Option.exists (fun c -> c.Token <> "" && Some c.Token <> (st.Remote |> Option.map (fun r -> r.Token)))
    let st = { st with Remote = config; Attempted = Set.empty; AuthHalted = st.AuthHalted && not newToken }
    // T18: records that failed on authentication return to pending once a new token is entered.
    let st, requeue =
        if not newToken then st, []
        else
            st.Records
            |> Map.toList
            |> List.filter (fun ((_, ws), r) -> ws = identity && r.Status = CausaGitHub.SyncFailed && (r.LastError |> Option.exists (fun e -> e.StartsWith("authentication", StringComparison.Ordinal))))
            |> List.fold
                (fun (s, fx) ((i, _), r) ->
                    let s, more = saveRecord i (CausaGitHub.Record.transition CausaGitHub.SyncPending (stamp s) "re-authenticated" "a new access token was entered" r) s
                    s, fx @ more)
                (st, [])
    match config with
    | None ->
        let c, st = Ids.correlation st SaveSettings
        st, requeue @ [ StorageRemove(c, Storage.settingsKey) ]
    | Some cfg ->
        let st, save = storageSet SaveSettings Storage.settingsKey (settingsDocument cfg) st
        let st, sync = syncNext st
        st, requeue @ save @ sync

/// "Check GitHub again": a failed record returns to pending (T18), an unknown one is resolved, then a sync cycle runs.
let private retrySync (st: AppState) : AppState * Effect list =
    match currentId st, st.Remote with
    | Some iid, Some cfg ->
        let st, save =
            match recordOf st iid with
            | Some r when r.Status = CausaGitHub.SyncFailed ->
                saveRecord iid (CausaGitHub.Record.transition CausaGitHub.SyncPending (stamp st) "retry" "retried by the person after a failure" r) st
            | _ -> st, []
        let st, sync = syncNext { st with Attempted = Set.empty }
        let notice =
            if not st.Online then "Offline: changes stay on this device and are sent when the connection returns."
            elif cfg.Token = "" then "Enter your access token in GitHub settings first; it is kept in memory for this session only."
            elif st.AuthHalted then "GitHub refused the access token: enter it again in GitHub settings."
            elif sync.IsEmpty && st.Flight.IsNone then "Nothing to send to GitHub now."
            else "Checking GitHub…"
        { st with Notice = notice }, save @ sync
    | _ -> st, []

// ---------------------------------------------------------------------------
// Commands: the only path is Decide.execute (stop-the-line rule)
// ---------------------------------------------------------------------------

let private refusalsOf (operation: string) (errors: DomainError list) : Refusal list =
    errors
    |> List.map (fun e ->
        let fault = Faults.ofDomainError operation e
        let (Aegis.FaultCode code) = fault.Code
        { Code = e.Code; Message = e.Message; Rule = e.Rule; FaultCode = code; Operation = operation })

/// Execute one command against an investigation; on success persist and sync.
// ---------------------------------------------------------------------------
// "Challenge the leading explanation": a deterministic, rule-based helper
// (R15.1, R15.3, R4.3). It is not a language model. Its only output is an
// agent proposal through Decide; a person accepts or rejects it.
// ---------------------------------------------------------------------------

[<RequireQualifiedAccess>]
module Challenge =
    let private standing (h: Hypothesis) =
        match h.State with
        | Proposed | Plausible | Weakened -> true
        | Supported | Falsified | Superseded -> false

    /// Rule: one or more hypotheses are Supported while alternatives still stand.
    /// Returns the proposal text and rationale for the first leading hypothesis.
    let suggestion (inv: Investigation) : (string * string) option =
        let all = inv.Hypotheses |> Map.toList |> List.map snd |> List.sortBy (fun h -> h.Provenance.RecordedAt)
        let alternatives = all |> List.filter standing
        match all |> List.tryFind (fun h -> h.State = Supported), alternatives with
        | Some leading, (_ :: _) ->
            let others = Words.join "; " (alternatives |> List.map (fun h -> "\"" + Words.clip 60 h.Statement.Value + "\""))
            Some(
                "What result would falsify \"" + leading.Statement.Value + "\"? Plan a check whose outcome would differ if " + others + " were true instead.",
                "Rule-based helper: a supported explanation with alternatives still standing should face a check that could prove it wrong (R15.3, R4.3)."
            )
        | _ -> None

    /// The same suggestion is not offered twice while it awaits a decision.
    let available (inv: Investigation) =
        match suggestion inv with
        | Some(text, _) -> not (inv.Proposals |> Map.exists (fun _ p -> p.State = ProposalActive && p.Text.Value = text))
        | None -> false

    /// R15.1: questions before conclusions, one per Policy cause blocker.
    let question (e: DomainError) =
        match e.Code with
        | "CAUSE.NO_SUPPORTING_EVIDENCE" -> "Which evidence directly supports this member of the cause? (" + e.Message + ")"
        | "CAUSE.MEMBER_NOT_SUPPORTED" -> "What evidence would move this hypothesis to supported — or rule it out? (" + e.Message + ")"
        | "CAUSE.ALTERNATIVE_NOT_ADDRESSED" -> "What observation would rule the alternative in or out? (" + e.Message + ")"
        | "CAUSE.OPEN_UNKNOWN" -> "Can this open question be answered before concluding? (" + e.Message + ")"
        | "CAUSE.OPEN_CONTRADICTION" -> "What explains the conflicting evidence? (" + e.Message + ")"
        | "CAUSE.UNVERIFIED_ASSUMPTION" -> "How can this assumption be checked? (" + e.Message + ")"
        | "CAUSE.NO_MECHANISM" -> "How, step by step, does the cause produce the problem? (" + e.Message + ")"
        | "CAUSE.NO_ROOT" -> "Which member is the root cause rather than a contributing factor? (" + e.Message + ")"
        | _ -> "Before concluding: " + e.Message

let private readOnlyRefusal (st: AppState) (iid: InvestigationId) (operation: string) : AppState * Effect list =
    let code, rule, message =
        if st.Screen = DemoScreen iid || not (st.Investigations.ContainsKey iid) then
            "DEMO.READ_ONLY", "Causa CAUSA-INV-13",
            "This is frozen demonstration data (causa-storage-v1.0.0) and is never changed. Create a working copy to make changes; the original stays as it is."
        else
            "STORAGE.LEGACY_READ_ONLY", "Causa RULES §6",
            "This investigation was saved before Causa storage, in a format that is kept unchanged rather than silently migrated. It can be read but not changed."
    { st with Refusals = refusalsOf operation [ DomainError.create code rule message ]; Notice = "Not done: read-only." }, []

let private run (operation: string) (actor: Actor) (notice: string) (fields: string list) (command: Command) (iid: InvestigationId) (st: AppState) : AppState * Effect list =
    match st.Screen, st.Investigations.ContainsKey iid, formatOf st iid with
    | DemoScreen _, _, _ | _, false, _ | _, _, LegacyFormat -> readOnlyRefusal st iid operation
    | _ ->
    let state = investigationOf st iid
    let commandId, st = Ids.next st
    let env =
        { CommandId = commandId
          Investigation = iid
          ExpectedVersion = state |> Option.map (fun i -> i.Version)
          Actor = actor
          RequestedAt = st.Now
          Command = command }
    match Decide.execute state env with
    | Error errors -> { st with Refusals = refusalsOf operation errors; Notice = "Not done: " + notice }, []
    | Ok events ->
        let history = (st.Investigations |> Map.tryFind iid |> Option.defaultValue []) @ events
        let candidate = { st with Investigations = st.Investigations |> Map.add iid history }
        // The change is applied only if the contract can represent and verify it (CAUSA.UNREPRESENTABLE etc.).
        match fileSetOf candidate iid with
        | Error e ->
            { st with Refusals = refusalsOf operation [ DomainError.create e.Code "Causa storage contract v1.0.0" (CausaData.describe e) ]; Notice = "Not done: " + notice }, []
        | Ok _ ->
            let st = { candidate with Refusals = []; Notice = notice } |> clear fields
            persistAfterChange iid st

/// A form that cannot yet form a command: explained like any refusal, with its Aegis code.
let private incomplete (st: AppState) (operation: string) (code: string) (rule: string) (message: string) : AppState * Effect list =
    { st with Refusals = refusalsOf operation [ DomainError.create code rule message ] }, []

let private inWorkspace (st: AppState) (f: InvestigationId -> AppState -> AppState * Effect list) =
    match currentId st with
    | Some iid -> f iid st
    | None -> st, []

let private parseKey<'Tag> (key: string option) : Id<'Tag> option = key |> Option.bind Id.tryParse<'Tag>

let private newId<'Tag> (st: AppState) : Id<'Tag> * AppState =
    let g, st = Ids.next st
    Id.create<'Tag> g, st

let private suffix (prefix: string) (name: string) =
    if name.StartsWith(prefix, StringComparison.Ordinal) then Some(name.Substring prefix.Length) else None

let private navigate (screen: Screen) (st: AppState) : AppState * Effect list =
    let c, st = Ids.correlation st NavigationAck
    { st with Screen = screen }, [ Navigate(c, ScreenRoute.format screen) ]

/// The shareable link of a screen: page origin + path + canonical hash (R9.10).
/// It is built from ids only, so it never carries evidence text or secrets (R9.9).
let shareLink (st: AppState) (screen: Screen) = st.BaseUrl + ScreenRoute.format screen

let private copyLink (screen: Screen) (st: AppState) : AppState * Effect list =
    let url = shareLink st screen
    let c, st = Ids.correlation st (CopyLink url)
    st, [ ClipboardWrite(c, url) ]

let private copyEvent (name: string) (key: string option) (st: AppState) : (AppState * Effect list) option =
    match currentId st, name with
    | Some iid, "copy.workspace" -> Some(copyLink (WorkspaceScreen iid) st)
    | Some iid, "copy.report" -> Some(copyLink (ReportScreen iid) st)
    | Some iid, _ when name.StartsWith("copy.", StringComparison.Ordinal) ->
        let segment = name.Substring 5
        match ScreenRoute.entitySegments |> List.tryFind (fun (s, _) -> s = segment), key |> Option.bind Id.tryParse<InvestigationTag> with
        | Some(_, kind), Some eid -> Some(copyLink (EntityScreen(iid, kind, eid.Value)) st)
        | _ -> Some(st, [])
    | _ -> None

let private startInvestigation (st: AppState) : AppState * Effect list =
    match schemasOf st with
    | None ->
        incomplete st "StartInvestigation" "CAUSA.SCHEMA_VIOLATION" "Causa storage contract v1.0.0"
            "The Causa storage schemas are not loaded yet (or could not be loaded), so a new investigation cannot be verified and saved. Reload the page and try again."
    | Some _ ->
    let iid, st = newId<InvestigationTag> st
    let oid, st = newId<ObservationTag> st
    let commandId, st = Ids.next st
    let env =
        { CommandId = commandId; Investigation = iid; ExpectedVersion = None; Actor = human; RequestedAt = st.Now
          Command = StartInvestigation(iid, draft st "title", oid, draft st "observation") }
    match Decide.execute None env with
    | Error errors -> { st with Refusals = refusalsOf "StartInvestigation" errors; Notice = "The investigation was not started." }, []
    | Ok events ->
        let st = { st with Investigations = st.Investigations |> Map.add iid events; Refusals = [] } |> clear [ "title"; "observation" ]
        // Starting means working: the kernel decides whether activation is legal.
        let st, saveEffects = run "ActivateInvestigation" human "Investigation started." [] ActivateInvestigation iid st
        let st, navEffects = navigate (WorkspaceScreen iid) st
        st, saveEffects @ navEffects

let private hypothesisCommand (name: string) (key: string option) (st: AppState) =
    match Words.hypothesisCommands |> List.tryFind (fun (n, _, _) -> n = name), parseKey<HypothesisTag> key with
    | Some(_, command, label), Some hid ->
        let evidence = guidDraft st "hypEvidence" |> Option.map Id.create<EvidenceTag> |> Option.toList
        inWorkspace st (run ("TransitionHypothesis." + name) human (label + ": done.") [ "hypReason"; "hypEvidence" ] (TransitionHypothesis(hid, command, draft st "hypReason", evidence)))
    | _ -> st, []

let private itemCommand (table: (string * 'C * string) list) (name: string) (build: 'C -> AppState -> Command option) (operation: string) (fields: string list) (st: AppState) =
    match table |> List.tryFind (fun (n, _, _) -> n = name) with
    | Some(_, command, label) ->
        match build command st with
        | Some cmd -> inWorkspace st (run (operation + "." + name) human (label + ": done.") fields cmd)
        | None -> st, []
    | None -> st, []

/// Copy a frozen demonstration into an editable working copy with the same id and the exact frozen history
/// (PROVIDER-GITHUB CAUSA-PROV-11). The frozen original is never written.
let private createWorkingCopy (key: string option) (st: AppState) : AppState * Effect list =
    // From the demonstration list the key names it; from the frozen view, it is the one shown.
    match parseKey<InvestigationTag> key |> Option.orElse (currentId st) with
    | Some iid when st.Investigations.ContainsKey iid -> navigate (WorkspaceScreen iid) { st with Notice = "This device already has a working copy of it." }
    | Some iid ->
        match demoOf st iid with
        | Some demo ->
            let st =
                { st with
                    Investigations = st.Investigations |> Map.add iid demo.Loaded.Events
                    Formats = st.Formats |> Map.add iid (CausaFormat(Some(CausaData.Pins.tag + "/" + demo.ScenarioKey)))
                    Refusals = []
                    Notice = "Working copy created. Changes are saved on this device; the frozen original is unchanged." }
            let st, save = persistAfterChange iid st
            let st, nav = navigate (WorkspaceScreen iid) st
            st, save @ nav
        | None -> st, []
    | None -> st, []

let private settingsFromDrafts (st: AppState) : RemoteConfig option =
    // The run is kept across saves; a first save chooses one (CAUSA-PROV-02).
    let runId =
        st.Remote
        |> Option.map (fun r -> r.RunId)
        |> Option.filter CausaGitHub.Workspace.runIdValid
        |> Option.defaultWith (fun () -> CausaGitHub.Workspace.newRunId "demo" st.Now st.Entropy)
    let branch = (optDraft st "setBranch" |> Option.orElse (st.Remote |> Option.map (fun r -> r.Branch)) |> Option.defaultValue "main").Trim()
    match optDraft st "setOwner" |> Option.orElse (st.Remote |> Option.map (fun r -> r.Owner)), optDraft st "setRepo" |> Option.orElse (st.Remote |> Option.map (fun r -> r.Repository)), optDraft st "setToken" with
    | Some owner, Some repo, Some token ->
        Some { Owner = owner.Trim(); Repository = repo.Trim(); Branch = branch; RunId = runId; Token = token.Trim() }
    | Some owner, Some repo, None ->
        // No new token: keep the one held in memory for this session (if any).
        let token = st.Remote |> Option.map (fun r -> r.Token) |> Option.defaultValue ""
        Some { Owner = owner.Trim(); Repository = repo.Trim(); Branch = branch; RunId = runId; Token = token }
    | _ -> None

/// UI events: field drafts ("f.<field>") and intents.
let private onEvent (name: string) (key: string option) (value: string option) (st: AppState) : AppState * Effect list =
    match suffix "f." name with
    | Some field -> { st with Drafts = st.Drafts |> Map.add field (Option.defaultValue "" value) }, []
    | None ->
    let st = { st with Notice = "" }
    match copyEvent name key st with
    | Some result -> result
    | None ->
    match name with
    | "start" when not st.Loaded ->
        // M6: nothing is created before the saved investigations have loaded.
        incomplete st "StartInvestigation" "LOAD.IN_PROGRESS" "R10.7" "Investigations saved on this device are still loading; try again in a moment."
    | "start" -> startInvestigation st
    | "dismissRefusals" -> { st with Refusals = [] }, []
    | "workingCopy.create" -> createWorkingCopy key st
    | "hyp.add" ->
        let hid, st = newId<HypothesisTag> st
        inWorkspace st (run "RecordHypothesis" human "Hypothesis added." [ "hypStatement"; "hypMechanism" ] (RecordHypothesis(hid, draft st "hypStatement", optDraft st "hypMechanism")))
    | "ai.add" ->
        let pid, st = newId<ProposalTag> st
        inWorkspace st (run "RecordProposal" assistant "AI-suggested hypothesis recorded for your review." [ "aiText"; "aiRationale" ] (RecordProposal(pid, ProposedHypothesis, draft st "aiText", optDraft st "aiRationale")))
    | "challenge" ->
        match currentId st |> Option.bind (investigationOf st) |> Option.bind Challenge.suggestion with
        | Some(text, rationale) ->
            let pid, st = newId<ProposalTag> st
            inWorkspace st (run "RecordProposal" challenger "Rule-based helper suggested a check that could falsify the leading explanation." [] (RecordProposal(pid, ProposedAction, text, Some rationale)))
        | None -> incomplete st "RecordProposal" "CHALLENGE.NOT_APPLICABLE" "R15.3" "There is no supported explanation with alternatives still standing to challenge."
    | "ai.accept" ->
        match parseKey<ProposalTag> key with
        | Some pid ->
            let target, st = Ids.next st
            inWorkspace st (run "AcceptProposal" human "Suggestion accepted as a hypothesis." [] (AcceptProposal(pid, target)))
        | None -> st, []
    | "ai.reject" ->
        match parseKey<ProposalTag> key with
        | Some pid -> inWorkspace st (run "RejectProposal" human "Suggestion rejected." [ "aiReason" ] (RejectProposal(pid, draft st "aiReason")))
        | None -> st, []
    | "ev.add" ->
        let eid, st = newId<EvidenceTag> st
        let content = if draft st "evKind" = "url" then UrlInput((draft st "evContent").Trim()) else TextInput(draft st "evContent")
        let category = choice Words.categories DirectObservation (draft st "evCategory")
        inWorkspace st (run "AdmitEvidence" human "Evidence added." [ "evKind"; "evContent"; "evCategory"; "evSource"; "evCaptured" ] (AdmitEvidence(eid, content, category, draft st "evSource", dateDraft st "evCaptured")))
    | "ev.supersede" ->
        match guidDraft st "supOld", guidDraft st "supNew" with
        | Some o, Some r ->
            inWorkspace st (run "SupersedeEvidence" human "Evidence replaced; the earlier item stays in the record." [ "supOld"; "supNew"; "supReason" ] (SupersedeEvidence(Id.create o, Id.create r, draft st "supReason")))
        | _ -> incomplete st "SupersedeEvidence" "FORM.INCOMPLETE" "form" "Choose the evidence to replace and the evidence that replaces it."
    | "rel.add" ->
        match guidDraft st "relEvidence", guidDraft st "relHypothesis" with
        | Some e, Some h ->
            let rid, st = newId<RelationshipTag> st
            let kind = choice Words.relationships Supports (draft st "relKind")
            inWorkspace st (run "RelateEvidence" human "Evidence linked to the hypothesis." [ "relEvidence"; "relHypothesis"; "relKind"; "relRationale" ] (RelateEvidence(rid, Id.create e, Id.create h, kind, optDraft st "relRationale")))
        | _ -> incomplete st "RelateEvidence" "FORM.INCOMPLETE" "form" "Choose both the evidence and the hypothesis to link."
    | "unk.add" ->
        let uid, st = newId<UnknownTag> st
        inWorkspace st (run "IdentifyUnknown" human "Open question added." [ "unkQuestion"; "unkMateriality" ] (IdentifyUnknown(uid, draft st "unkQuestion", choice Words.materialities Material (draft st "unkMateriality"))))
    | "con.add" ->
        let cid, st = newId<ContradictionTag> st
        let evidence = guidDraft st "conEvidence" |> Option.map Id.create<EvidenceTag> |> Option.toList
        inWorkspace st (run "RecordContradiction" human "Contradiction recorded." [ "conStatement"; "conEvidence"; "conMateriality" ] (RecordContradiction(cid, draft st "conStatement", evidence, choice Words.materialities Material (draft st "conMateriality"))))
    | "pred.add" ->
        match guidDraft st "predHypothesis" with
        | Some h ->
            let pid, st = newId<PredictionTag> st
            inWorkspace st (run "RecordPrediction" human "Prediction recorded." [ "predHypothesis"; "predStatement"; "predStrength" ] (RecordPrediction(pid, Id.create h, draft st "predStatement", choice Words.strengths Expected (draft st "predStrength"), Prospective)))
        | None -> incomplete st "RecordPrediction" "FORM.INCOMPLETE" "form" "Choose the hypothesis the prediction follows from."
    | "act.add" ->
        let aid, st = newId<ActionTag> st
        let targets = guidDraft st "actHypothesis" |> Option.map Id.create<HypothesisTag> |> Option.toList
        inWorkspace st (run "RecordAction" human "Test or experiment planned." [ "actKind"; "actPurpose"; "actHypothesis" ] (RecordAction(aid, choice Words.actionKinds Test (draft st "actKind"), draft st "actPurpose", targets)))
    | "find.add" ->
        match guidDraft st "findAction" with
        | Some a ->
            let fid, st = newId<FindingTag> st
            inWorkspace st (run "RecordFinding" human "Finding recorded." [ "findAction"; "findResult"; "findInterpretation" ] (RecordFinding(fid, Id.create a, draft st "findResult", optDraft st "findInterpretation")))
        | None -> incomplete st "RecordFinding" "FORM.INCOMPLETE" "form" "Choose the test or experiment this finding came from."
    | "neg.add" ->
        let nid, st = newId<NegativeKnowledgeTag> st
        let basis = guidDraft st "negEvidence" |> Option.map Id.create<EvidenceTag> |> Option.toList
        inWorkspace st (run "EstablishNegativeKnowledge" human "Ruled-out knowledge recorded." [ "negStatement"; "negScope"; "negEvidence" ] (EstablishNegativeKnowledge(nid, draft st "negStatement", draft st "negScope", basis)))
    | "cause.add" ->
        let cid, st = newId<CausalAssertionTag> st
        let members =
            (guidDraft st "causeRoot" |> Option.map (fun g -> { Hypothesis = Id.create g; Role = RootCause }) |> Option.toList)
            @ (guidDraft st "causeFactor" |> Option.map (fun g -> { Hypothesis = Id.create g; Role = ContributingFactor }) |> Option.toList)
        inWorkspace st (run "RecordCausalAssertion" human "Cause proposed." [ "causeOutcome"; "causeRoot"; "causeFactor"; "causeMechanism" ] (RecordCausalAssertion(cid, draft st "causeOutcome", members, optDraft st "causeMechanism")))
    | "cause.confirm" ->
        match parseKey<CausalAssertionTag> key with
        | Some cid -> inWorkspace st (run "ConfirmCause" human "Root cause confirmed." [] (ConfirmCause cid))
        | None -> st, []
    | "corr.add" ->
        let caid, st = newId<CorrectiveActionTag> st
        let targets = guidDraft st "corrCause" |> Option.map Id.create<CausalAssertionTag> |> Option.toList
        inWorkspace st (run "RecordCorrectiveAction" human "Corrective action added." [ "corrKind"; "corrDescription"; "corrCause" ] (RecordCorrectiveAction(caid, choice Words.correctionKinds Correction (draft st "corrKind"), draft st "corrDescription", targets)))
    | "crit.add" ->
        match guidDraft st "critCorrection" with
        | Some ca ->
            let crid, st = newId<CriterionTag> st
            inWorkspace st (run "RecordCriterion" human "Success criterion added." [ "critCorrection"; "critExpected"; "critMethod" ] (RecordCriterion(crid, Id.create ca, draft st "critExpected", optDraft st "critMethod")))
        | None -> incomplete st "RecordCriterion" "FORM.INCOMPLETE" "form" "Choose the corrective action this criterion checks."
    | "ver.add" ->
        match guidDraft st "verCriterion" with
        | Some cr ->
            let vid, st = newId<VerificationTag> st
            let evidence = guidDraft st "verEvidence" |> Option.map Id.create<EvidenceTag> |> Option.toList
            inWorkspace st (run "RecordVerification" human "Verification recorded." [ "verCriterion"; "verOutcome"; "verEvidence" ] (RecordVerification(vid, Id.create cr, choice Words.outcomes Satisfied (draft st "verOutcome"), evidence)))
        | None -> incomplete st "RecordVerification" "FORM.INCOMPLETE" "form" "Choose the success criterion being verified."
    | "concl.add" ->
        let cid, st = newId<ConclusionTag> st
        let causes = guidDraft st "conclCause" |> Option.map Id.create<CausalAssertionTag> |> Option.toList
        inWorkspace st (run "RecordConclusion" human "Conclusion recorded." [ "conclStatement"; "conclStrength"; "conclCause"; "conclResidual" ] (RecordConclusion(cid, draft st "conclStatement", choice Words.conclusions SupportedCause (draft st "conclStrength"), causes, optDraft st "conclResidual")))
    | "settings.save" ->
        match settingsFromDrafts st with
        | Some config ->
            match CausaGitHub.Workspace.check { Owner = config.Owner; Repo = config.Repository; Branch = config.Branch; RunId = config.RunId } with
            | Error reason -> incomplete st "SaveSettings" "SETTINGS.INVALID" "settings" reason
            | Ok _ ->
                let notice =
                    if config.Token = "" then "Repository saved. Enter an access token to publish; it is kept in memory for this session only."
                    else "GitHub settings saved. Working copies are published to run " + config.RunId + ". The access token is kept in memory for this session only and is never saved on this device."
                saveSettings (Some config) ({ st with Notice = notice } |> clear [ "setToken" ])
        | None -> incomplete st "SaveSettings" "SETTINGS.INCOMPLETE" "settings" "Enter the owner, repository and an access token."
    | "settings.clear" -> saveSettings None ({ st with Notice = "GitHub settings removed. Investigations stay on this device." } |> clear [ "setOwner"; "setRepo"; "setBranch"; "setToken" ])
    | "sync.retry" -> retrySync st
    | "sync.adopt" ->
        // CAUSA-PROV-29: only on the person's explicit choice; this device's version is kept as a conflict copy.
        match currentId st with
        | Some iid ->
            match recordOf st iid with
            | Some r when r.Status = CausaGitHub.SyncConflicted && r.Remote.IsSome ->
                let st, fx = adoptConfirmed iid r.Remote.Value (match formatOf st iid with CausaFormat o -> o | LegacyFormat -> None) st
                { st with Notice = "Using GitHub's version: this device's version is first kept as a separate conflict copy, then replaced." }, fx
            | _ -> st, []
        | None -> st, []
    | "sync.newRun" ->
        // CAUSA-PROV-29 §5: continue in a new run; nothing in the previous run is changed.
        match st.Remote with
        | Some cfg ->
            let runId = CausaGitHub.Workspace.newRunId "demo" st.Now st.Entropy
            saveSettings (Some { cfg with RunId = runId }) { st with Notice = "New run " + runId + ": investigations are published there as new copies. Nothing in the previous run was changed." }
        | None -> st, []
    | "sync.recover" ->
        match st.Remote with
        | Some cfg when cfg.Token <> "" && st.Loaded && st.Flight.IsNone && (schemasOf st).IsSome ->
            let st, fx = startRecover cfg st
            { st with Notice = "Reading run " + cfg.RunId + " from GitHub…" }, fx
        | Some cfg when cfg.Token = "" -> { st with Notice = "Enter your access token first; it is kept in memory for this session only." }, []
        | _ -> { st with Notice = "GitHub is busy or not configured; try again in a moment." }, []
    | _ ->
        match suffix "hyp." name with
        | Some command -> hypothesisCommand command key st
        | None ->
        match suffix "unk." name, parseKey<UnknownTag> key with
        | Some command, Some uid -> itemCommand Words.unknownCommands command (fun c s -> Some(TransitionUnknown(uid, c, draft s "unkBasis"))) "TransitionUnknown" [ "unkBasis" ] st
        | _ ->
        match suffix "con." name, parseKey<ContradictionTag> key with
        | Some command, Some cid -> itemCommand Words.contradictionCommands command (fun c s -> Some(TransitionContradiction(cid, c, draft s "conBasis"))) "TransitionContradiction" [ "conBasis" ] st
        | _ ->
        match suffix "act." name, parseKey<ActionTag> key with
        | Some command, Some aid -> itemCommand Words.actionCommands command (fun c _ -> Some(TransitionAction(aid, c))) "TransitionAction" [] st
        | _ ->
        match suffix "corr." name, parseKey<CorrectiveActionTag> key with
        | Some command, Some caid -> itemCommand Words.correctionCommands command (fun c _ -> Some(TransitionCorrection(caid, c))) "TransitionCorrection" [] st
        | _ ->
        match suffix "life." name with
        | Some command ->
            let build c (s: AppState) =
                match c with
                | ReopenInvestigation _ -> Some(ReopenInvestigation(draft s "reopenBasis"))
                | MarkAwaitingEvidence _ -> Some(MarkAwaitingEvidence "Waiting for evidence.")
                | other -> Some other
            itemCommand LifecycleCommands.all command build "Lifecycle" [ "reopenBasis" ] st
        | None -> st, []

// ---------------------------------------------------------------------------
// Effect results
// ---------------------------------------------------------------------------

/// Pure decoding of dist/factory.json; anything unreadable is an explained failure.
let decodeFactory (text: string) : Result<FactoryInfo, string> =
    match JsonIn.parse text with
    | None -> Error "factory.json is not JSON"
    | Some root ->
        let field name (n: JsonNode option) = JsonIn.child name n |> JsonIn.str |> Option.defaultValue ""
        match JsonIn.child "schema" (Some root) |> JsonIn.str with
        | Some "rci.factory/v1" ->
            let tests =
                match JsonIn.child "tests" (Some root) with
                | Some(:? JsonObject as o) ->
                    let t = Some(o :> JsonNode)
                    let count name = JsonIn.child name t |> JsonIn.int |> Option.defaultValue 0
                    Some
                        { RunCommit = field "commit" t
                          Total = count "tests"
                          Passed = count "passed"
                          Failed = count "failed"
                          Tested = count "tested"
                          Untested = count "untested"
                          Failing = count "failing"
                          Suites = field "suites" t }
                | _ -> None
            Ok
                { Label = field "label" (Some root)
                  Commit = field "commit" (Some root)
                  BuiltAt = field "builtAt" (Some root)
                  Experiment = field "experiment" (Some root)
                  Requirements =
                    JsonIn.child "requirements" (Some root) |> JsonIn.list
                    |> List.map (fun r ->
                        { ReqId = field "id" r; ReqTitle = field "title" r; ReqScope = field "scope" r; ReqSlice = field "slice" r; Coverage = field "coverage" r })
                  WorkItems =
                    JsonIn.child "workItems" (Some root) |> JsonIn.list
                    |> List.map (fun w -> field "id" w, field "title" w, field "status" w)
                  Tests = tests
                  BuildStatus = (match field "buildStatus" (Some root) with "" -> "unknown" | v -> v) }
        | other -> Error("unsupported factory schema " + Option.defaultValue "(none)" other)

/// Entering the factory view requests ./factory.json once (Limen Http effect).
let private ensureFactory (st: AppState) : AppState * Effect list =
    match st.Screen, st.Factory with
    | FactoryScreen, (FactoryNotRequested | FactoryFailed _) ->
        let c, st = Ids.correlation st FactoryLoad
        { st with Factory = FactoryLoading }, [ HttpCall(c, "GET", "./factory.json", [], None) ]
    | _ -> st, []

let private finishLoading (st: AppState) =
    let loading =
        st.Pending |> Map.exists (fun _ op -> match op with LoadIndex | LoadInvestigation _ | LoadRecord _ | LoadSettings | LoadDataset -> true | _ -> false)
    if loading || st.Loaded then st, []
    else
        let st = { st with Loaded = true }
        let st, index = if st.IndexDirty then saveIndex st else st, []
        // CAUSA-PROV-26 §4: after a restart, unknown outcomes are resolved before anything pending is sent.
        let st, send = syncNext st
        st, index @ send

/// Adopt a stored Causa file set only if every reader stage passes; otherwise it is reported and left untouched.
let private adoptFileSet (iid: InvestigationId) (text: string) (st: AppState) : AppState =
    match st.Demo, schemasOf st with
    | DemoLoading, _ -> { st with Deferred = st.Deferred |> Map.add iid text }
    | DemoFailed reason, _ ->
        { st with Unreadable = st.Unreadable |> Map.add iid ("the Causa storage schemas could not be loaded (" + reason + "), so it cannot be verified") }
    | DemoReady _, None -> { st with Unreadable = st.Unreadable |> Map.add iid "the Causa storage schemas are unavailable, so it cannot be verified" }
    | _, Some schemas ->
        let verified =
            CausaData.decodeFileSet text
            |> Result.bind (fun (files, origin) ->
                if files.DirectoryId <> Id.text iid then Causa.Contract.CausaError.fail Causa.Contract.Codes.manifestMismatch "the stored file set names a different investigation"
                else Causa.Contract.Store.validate schemas files |> Result.map (fun loaded -> loaded, origin))
        match verified with
        | Ok(loaded, origin) ->
            { st with
                Investigations = st.Investigations |> Map.add iid loaded.Events
                Formats = st.Formats |> Map.add iid (CausaFormat origin)
                StoredVersion = st.StoredVersion |> Map.add iid loaded.State.Version
                Local = st.Local |> Map.add iid SavedLocally }
        | Error e -> { st with Unreadable = st.Unreadable |> Map.add iid ("it failed Causa verification: " + CausaData.describe e) }

let private adoptDeferred (st: AppState) : AppState =
    st.Deferred |> Map.fold (fun s iid text -> adoptFileSet iid text s) { st with Deferred = Map.empty }

/// Write this window's file set only as a fast-forward of the device copy: Causa histories are append-only canonical
/// bytes, so the device copy is safe to replace exactly when its events.ndjson is a byte prefix of this window's. A
/// copy another window extended differently is never overwritten (lost-update protection across windows), while this
/// window's own earlier writes, landed or still in flight, always qualify.
let private onCheckBeforeSave (iid: InvestigationId) (stored: string option) (st: AppState) : AppState * Effect list =
    match fileSetOf st iid with
    | Error e -> { st with Local = st.Local |> Map.add iid (SaveFailed(CausaData.describe e)) }, []
    | Ok(files, loaded) ->
        let text = CausaData.encodeFileSet (match formatOf st iid with CausaFormat o -> o | LegacyFormat -> None) files
        let device = stored |> Option.map (fun t -> if CausaData.isFileSet t then CausaData.decodeFileSet t |> Result.toOption |> Option.map fst else None)
        let isPrefix (older: Causa.Contract.InvestigationFiles) = files.Events.AsSpan().StartsWith(older.Events.AsSpan())
        match device with
        | Some None ->
            { st with Local = st.Local |> Map.add iid (SaveFailed "the device holds an investigation with this id in another format; it was not overwritten") }, []
        | None -> let c, st = Ids.correlation st (SaveFileSet(iid, loaded.State.Version)) in st, [ StorageSet(c, Storage.investigationKey iid, text) ]
        | Some(Some older) when isPrefix older ->
            let c, st = Ids.correlation st (SaveFileSet(iid, loaded.State.Version))
            st, [ StorageSet(c, Storage.investigationKey iid, text) ]
        | Some(Some _) ->
            // Another window wrote a different history: refuse, keep this window's work in memory and as a conflict copy.
            let reason =
                "Not saved: another window changed this investigation on this device. Your changes are kept in this window; a separate conflict copy is being saved."
            let st = { st with Local = st.Local |> Map.add iid (SaveFailed reason); SaveConflicts = st.SaveConflicts |> Map.add iid reason }
            let c, st = Ids.correlation st (SaveConflictCopy iid)
            st, [ StorageSet(c, Storage.conflictKey iid (string loaded.State.Version + "-" + stamp st + "-" + string st.NextCorrelation), text) ]

let private onStorage (op: PendingOp) (result: Result<string option, string>) (st: AppState) : AppState * Effect list =
    match op, result with
    | CheckBeforeSave iid, Ok stored -> onCheckBeforeSave iid stored st
    | CheckBeforeSave iid, Error reason -> { st with Local = st.Local |> Map.add iid (SaveFailed reason) }, []
    | SaveFileSet(iid, version), Ok _ ->
        { st with Local = st.Local |> Map.add iid SavedLocally; StoredVersion = st.StoredVersion |> Map.add iid version; SaveConflicts = st.SaveConflicts |> Map.remove iid }, []
    | SaveFileSet(iid, _), Error reason -> { st with Local = st.Local |> Map.add iid (SaveFailed reason) }, []
    | SaveConflictCopy iid, Ok _ ->
        let reason = "Not saved: another window changed this investigation on this device. Your changes are kept in this window and in a separate conflict copy on this device; reload to see the other version."
        { st with Local = st.Local |> Map.add iid (SaveFailed reason); SaveConflicts = st.SaveConflicts |> Map.add iid reason }, []
    | SaveConflictCopy iid, Error why ->
        let reason = "Not saved: another window changed this investigation on this device, and the conflict copy could not be saved (" + why + "). Your changes exist only in this window: do not close it."
        { st with Local = st.Local |> Map.add iid (SaveFailed reason); SaveConflicts = st.SaveConflicts |> Map.add iid reason }, []
    | AdoptRead(iid, c, origin), Ok stored -> onAdoptRead iid c origin stored st
    | AdoptRead _, Error why -> { st with Notice = "GitHub's version was not used: this device's copy could not be read first (" + why + ")." }, []
    | AdoptCopy(iid, c, origin, rest), Ok _ -> adoptCopies iid c origin rest st
    | AdoptCopy _, Error why ->
        { st with Notice = "GitHub's version was not used: this device's version could not be kept as a conflict copy (" + why + "). Nothing was replaced." }, []
    | LoadInvestigation iid, Ok(Some text) when CausaData.isFileSet text -> finishLoading (adoptFileSet iid text st)
    | LoadSettings, Ok(Some text) ->
        // Settings from before Causa storage have no run: one is chosen now (CAUSA-PROV-02) and saved.
        let remote =
            readSettings text
            |> Option.map (fun cfg -> if CausaGitHub.Workspace.runIdValid cfg.RunId then cfg else { cfg with RunId = CausaGitHub.Workspace.newRunId "demo" st.Now st.Entropy })
        // A token persisted by an older build is erased from this device (R11.3).
        let st, scrub =
            match remote, JsonIn.child "token" (JsonIn.parse text), JsonIn.child "runId" (JsonIn.parse text) |> JsonIn.str with
            | Some cfg, Some _, _ -> storageSet SaveSettings Storage.settingsKey (settingsDocument cfg) st
            | Some cfg, None, stored when stored <> Some cfg.RunId -> storageSet SaveSettings Storage.settingsKey (settingsDocument cfg) st
            | _ -> st, []
        let st, more = finishLoading { st with Remote = remote }
        st, scrub @ more
    | LoadRecord iid, Ok(Some text) ->
        match CausaGitHub.Record.decodeBook text with
        | Ok records ->
            // T19/T20: a publish interrupted by the restart is pending, or unknown once its PATCH may have been sent.
            let recovered = records |> List.map (CausaGitHub.Record.recover (stamp st))
            let st = recovered |> List.fold (fun s r -> withRecord iid r s) st
            let st, save =
                match List.zip records recovered |> List.tryFind (fun (a, b) -> a.Status <> b.Status) with
                | Some(_, r) -> saveRecord iid r st
                | None -> st, []
            let st, more = finishLoading st
            st, save @ more
        | Error e ->
            finishLoading
                { st with
                    RecordUnreadable = st.RecordUnreadable |> Set.add iid
                    Notice = "The GitHub sync state of one investigation could not be read (" + e + "); it is left untouched and that investigation is not published." }
    | LoadRecord _, (Ok None | Error _) -> finishLoading st
    | SaveRecord _, Error reason -> { st with Notice = "Saving the GitHub sync state on this device failed (" + reason + ")." }, []
    | LoadIndex, Ok(Some text) ->
        match IndexCodec.decode text with
        | Ok entries ->
            let st = { st with StoredIndex = entries |> List.map (fun e -> e.Id, e) |> Map.ofList }
            let st, effects =
                entries
                |> List.fold
                    (fun (s, fx) (e: IndexCodec.Entry) ->
                        let c, s = Ids.correlation s (LoadInvestigation e.Id)
                        let r, s = Ids.correlation s (LoadRecord e.Id)
                        s, fx @ [ StorageGet(c, Storage.investigationKey e.Id); StorageGet(r, CausaGitHub.Record.key (Id.text e.Id)) ])
                    (st, [])
            let st, more = finishLoading st
            st, effects @ more
        | Error e -> finishLoading { st with IndexUnreadable = true; Notice = "The saved list of investigations could not be read (" + e + "); it is left untouched." }
    | LoadInvestigation iid, Ok(Some text) ->
        match EventCodec.decode text with
        | Ok(id, events) when id = iid && not events.IsEmpty ->
            // m4: a history is adopted only if the kernel can verify it; the stored data is left untouched.
            match History.validate events with
            | [] -> finishLoading { st with Investigations = st.Investigations |> Map.add iid events; Local = st.Local |> Map.add iid SavedLocally; Formats = st.Formats |> Map.add iid LegacyFormat }
            | errors ->
                finishLoading { st with Unreadable = st.Unreadable |> Map.add iid ("it could not be verified (" + Words.join ", " (errors |> List.map (fun e -> e.Code) |> List.distinct) + ")") }
        | Ok _ -> finishLoading { st with Unreadable = st.Unreadable |> Map.add iid "the stored document names a different investigation" }
        | Error e -> finishLoading { st with Unreadable = st.Unreadable |> Map.add iid e }
    | LoadInvestigation iid, Ok None -> finishLoading { st with Unreadable = st.Unreadable |> Map.add iid "its stored document is missing" }
    | (LoadSettings | LoadIndex), Ok None -> finishLoading st
    | (LoadSettings | LoadIndex | LoadInvestigation _), Error reason ->
        finishLoading { st with Notice = "This device's storage is unavailable (" + reason + "); work will not survive a reload." }
    | SaveInvestigation iid, Ok _ -> { st with Local = st.Local |> Map.add iid SavedLocally }, []
    | SaveInvestigation iid, Error reason -> { st with Local = st.Local |> Map.add iid (SaveFailed reason) }, []
    | (SaveIndex | SaveSettings), Error reason -> { st with Notice = "Saving on this device failed (" + reason + ")." }, []
    | _ -> st, []

// ---------------------------------------------------------------------------
// Projection
// ---------------------------------------------------------------------------

type private Labels =
    { Hyp: Map<Guid, string>
      Ev: Map<Guid, string>
      Cause: Map<Guid, string>
      Corr: Map<Guid, string>
      Crit: Map<Guid, string>
      Act: Map<Guid, string> }

let private ordinals (events: Event list) (prefix: string) (pick: EventBody -> Guid option) : Map<Guid, string> =
    events
    |> List.choose (fun e -> pick e.Body)
    |> List.distinct
    |> List.mapi (fun i g -> g, prefix + string (i + 1))
    |> Map.ofList

let private labelsOf (events: Event list) : Labels =
    { Hyp =
        ordinals events "H" (fun b ->
            match b with
            | HypothesisRecorded(h, _, _) -> Some h.Value
            | _ -> None)
      Ev = ordinals events "E" (fun b -> match b with EvidenceAdmitted(e, _, _, _, _) -> Some e.Value | _ -> None)
      Cause = ordinals events "C" (fun b -> match b with CausalAssertionRecorded(c, _, _, _) -> Some c.Value | _ -> None)
      Corr = ordinals events "A" (fun b -> match b with CorrectiveActionRecorded(c, _, _, _) -> Some c.Value | _ -> None)
      Crit = ordinals events "K" (fun b -> match b with CriterionRecorded(c, _, _, _) -> Some c.Value | _ -> None)
      Act = ordinals events "T" (fun b -> match b with ActionRecorded(a, _, _, _) -> Some a.Value | _ -> None) }

let private lbl (m: Map<Guid, string>) (g: Guid) = Map.tryFind g m |> Option.defaultValue "?"

let private evidenceText (e: Evidence) =
    match e.Content with
    | TextEvidence t -> t.Value
    | ArtifactEvidence uri -> uri

/// Plain-language account of one event for the timeline (R14.1).
let private describe (inv: Investigation) (l: Labels) (body: EventBody) : string =
    let hyp (h: HypothesisId) =
        lbl l.Hyp h.Value + " " + (inv.Hypotheses |> Map.tryFind h |> Option.map (fun x -> "\"" + Words.clip 50 x.Statement.Value + "\"") |> Option.defaultValue "")
    let ev (e: EvidenceId) = lbl l.Ev e.Value
    match body with
    | InvestigationStarted(title, _, _) -> "Investigation started: " + title.Value
    | LifecycleChanged(f, t, _, reason) -> "Status changed from " + Words.lifecycle f + " to " + Words.lifecycle t + (reason |> Option.map (fun r -> " (" + r + ")") |> Option.defaultValue "")
    | ObservationRecorded(_, t, _) -> "Observation recorded: " + Words.clip 80 t.Value
    | HypothesisRecorded(h, _, _) -> "Hypothesis added: " + hyp h
    | HypothesisTransitioned(h, c) ->
        hyp h + " changed from " + Words.hypothesis c.From + " to " + Words.hypothesis c.To + " because: " + c.Reason.Value
        + (if c.Evidence.IsEmpty then "" else " (evidence " + Words.join ", " (c.Evidence |> List.map ev) + ")")
    | EvidenceAdmitted(e, _, cat, src, _) -> "Evidence " + ev e + " added (" + Words.category cat + ", source: " + src.Value + ")"
    | EvidenceSuperseded(o, r, why) -> "Evidence " + ev o + " replaced by " + ev r + ": " + why.Value
    | EvidenceVerificationAssessed(e, s) -> "Evidence " + ev e + " checked: " + Words.verificationStatus s
    | EvidenceRelated(_, e, h, k, _) -> "Evidence " + ev e + " " + Words.relationship k + " " + hyp h
    | UnknownIdentified(_, q, m) -> "Open question (" + Words.materiality m + "): " + Words.clip 80 q.Value
    | UnknownTransitioned(u, s, b) ->
        "Open question " + (inv.Unknowns |> Map.tryFind u |> Option.map (fun x -> "\"" + Words.clip 50 x.Question.Value + "\"") |> Option.defaultValue "") + " is now " + Words.unknown s + ": " + b.Value
    | AssumptionRecorded(_, p, _) -> "Assumption recorded: " + Words.clip 80 p.Value
    | AssumptionTransitioned _ -> "Assumption updated"
    | ContradictionRecorded(_, s, _, _) -> "Contradiction recorded: " + Words.clip 80 s.Value
    | ContradictionTransitioned(_, s, b) -> "Contradiction is now " + Words.contradiction s + ": " + b.Value
    | PredictionRecorded(_, h, s, _, _) -> "Prediction for " + hyp h + ": " + Words.clip 80 s.Value
    | ActionRecorded(a, k, p, _) -> Words.actionKind k + " " + lbl l.Act a.Value + " planned: " + Words.clip 80 p.Value
    | ActionTransitioned(a, s) -> "Test " + lbl l.Act a.Value + " is now " + Words.action s
    | FindingRecorded(_, a, r, _) -> "Finding from " + lbl l.Act a.Value + ": " + Words.clip 80 r.Value
    | NegativeKnowledgeEstablished(_, s, _, _) -> "Ruled out: " + Words.clip 80 s.Value
    | CausalAssertionRecorded(c, o, _, _) -> "Cause " + lbl l.Cause c.Value + " proposed for: " + Words.clip 80 o.Value
    | CausalAssertionConfirmed(c, _) -> "Cause " + lbl l.Cause c.Value + " confirmed"
    | CausalAssertionReopened(c, r) -> "Cause " + lbl l.Cause c.Value + " reopened: " + r.Value
    | CorrectiveActionRecorded(c, k, d, _) -> Words.correctionKind k + " " + lbl l.Corr c.Value + " added: " + Words.clip 80 d.Value
    | CorrectiveActionTransitioned(c, s) -> "Corrective action " + lbl l.Corr c.Value + " is now " + Words.correction s
    | CriterionRecorded(c, _, e, _) -> "Success criterion " + lbl l.Crit c.Value + ": " + Words.clip 80 e.Value
    | VerificationRecorded(_, c, o, e) ->
        "Verification of " + lbl l.Crit c.Value + ": " + Words.outcome o + (if e.IsEmpty then "" else " (evidence " + Words.join ", " (e |> List.map ev) + ")")
    | ConclusionRecorded(_, s, st, _, _) -> "Conclusion (" + Words.conclusion st + "): " + Words.clip 80 s.Value
    | ProposalRecorded(_, _, t, _) -> "AI suggestion recorded: " + Words.clip 80 t.Value
    | ProposalWasAccepted _ -> "AI suggestion accepted by a person"
    | ProposalWasRejected(_, r) -> "AI suggestion rejected: " + r.Value

let private item (fields: (string * J) list) = JObj fields
let private s (v: string) = JStr v
let private b (v: bool) = JBool v
let private n (v: int) = JNum(int64 v)

/// Which states a command is legal from, read from the kernel table.
let private legalFrom (table: 'S -> 'C -> 'S option) (states: 'S list) (word: 'S -> string) (command: 'C) =
    states |> List.filter (fun st -> table st command |> Option.isSome) |> List.map word |> Words.states

let private placeholder: Investigation option =
    match NonEmptyText.tryCreate "—" with
    | Some t ->
        let iid = Id.create<InvestigationTag> (Guid("00000000-0000-0000-0000-0000000000ff"))
        Some(Evolve.apply None { Sequence = 0L; Investigation = iid; CommandId = Guid.Empty; Actor = Actor.system; At = DateTimeOffset.UnixEpoch; Body = InvestigationStarted(t, Id.create (Guid.Empty), t) })
    | None -> None

let private remoteText (st: AppState) (iid: InvestigationId) =
    match st.Remote, formatOf st iid with
    | None, _ -> "Local only — not connected to GitHub", "unknown"
    | Some _, LegacyFormat -> "Earlier format — read-only on this device and not published", "unknown"
    | Some cfg, _ ->
        let where = " (" + cfg.Owner + "/" + cfg.Repository + "@" + cfg.Branch + ", run " + cfg.RunId + ")"
        let state = recordOf st iid |> Option.map (shownState st iid) |> Option.defaultValue CausaGitHub.SyncPending
        let tone =
            match state with
            | CausaGitHub.SyncConfirmed -> "ok"
            | CausaGitHub.SyncConflicted | CausaGitHub.SyncRefused | CausaGitHub.SyncFailed -> "blocked"
            | _ -> "attention"
        let label = CausaGitHub.SyncState.label state
        match state with
        | CausaGitHub.SyncPending when cfg.Token = "" -> label + " — enter your access token in GitHub settings; it is not kept after a reload", tone
        | CausaGitHub.SyncPending when not st.Online -> label + " — offline", tone
        | _ -> label + where, tone

let private syncKeys (st: AppState) (iid: InvestigationId) : (string * J) list =
    let record = recordOf st iid
    let state = record |> Option.map (shownState st iid)
    let detail = record |> Option.bind (fun r -> r.LastError) |> Option.map (redact st)
    let log =
        record
        |> Option.map (fun r -> r.Log |> List.rev |> List.truncate 8)
        |> Option.defaultValue []
        |> List.mapi (fun i e -> item [ "id", s (string i); "status", s (CausaGitHub.SyncState.wire e.Status); "at", s e.At; "cause", s e.Cause; "detail", s (redact st e.Detail) ])
    let path =
        match st.Remote with
        | Some cfg -> CausaGitHub.Workspace.prefix (workspaceOf cfg) (Id.text iid)
        | None -> ""
    [ "syncState", s (state |> Option.map CausaGitHub.SyncState.wire |> Option.defaultValue (if st.Remote.IsSome then "pending" else "local"))
      "syncDetail", s (detail |> Option.defaultValue "")
      "hasSyncDetail", b detail.IsSome
      "canRetrySync",
      b (st.Remote.IsSome && formatOf st iid <> LegacyFormat && (match state with Some CausaGitHub.SyncConfirmed | Some CausaGitHub.SyncSending | Some CausaGitHub.SyncConflicted | Some CausaGitHub.SyncRefused -> false | _ -> true))
      "canAdoptRemote", b (record |> Option.exists (fun r -> r.Status = CausaGitHub.SyncConflicted && r.Remote.IsSome))
      "canStartNewRun", b (match state with Some CausaGitHub.SyncConflicted | Some CausaGitHub.SyncRefused -> true | _ -> false)
      "syncPath", s path
      "hasSyncPath", b (path <> "")
      "syncLog", JArr log
      "hasSyncLog", b (not log.IsEmpty) ]

let private localText (st: AppState) (iid: InvestigationId) =
    match Map.tryFind iid st.Local with
    | Some SavedLocally -> "Saved on this device"
    | Some Saving -> "Saving on this device…"
    | Some(SaveFailed r) -> "Not saved on this device (" + r + ")"
    // N9: nothing claims a save that has not been recorded.
    | None -> "Not saved yet"

/// Plain summaries of what the replayed state already records; they describe, they never decide.
let private verificationSummary (inv: Investigation) =
    let corrections = inv.CorrectiveActions |> Map.toList |> List.map snd
    if corrections |> List.exists (fun c -> c.State = VerifiedEffective) then "Fix verified effective", "success"
    elif corrections |> List.exists (fun c -> c.State = AwaitingVerificationOfEffect) then "Awaiting verification of the fix", "info"
    elif corrections |> List.exists (fun c -> c.State = VerificationFailed || c.State = ReworkRequired) then "Verification failed; rework needed", "danger"
    elif not corrections.IsEmpty then "Fix recorded, not yet verified", "warning"
    else "No fix recorded yet", "neutral"

let private causeSummary (inv: Investigation) =
    let causes = inv.Causes |> Map.toList |> List.map snd
    if causes |> List.exists (fun c -> c.State = CauseConfirmed) then "Cause confirmed"
    elif causes |> List.exists (fun c -> c.State = CauseReopened) then "Cause reopened"
    elif not causes.IsEmpty then "Cause proposed, not confirmed"
    else "No cause asserted yet"

let private openHypotheses (inv: Investigation) =
    inv.Hypotheses |> Map.filter (fun _ h -> h.State <> Falsified && h.State <> Superseded) |> Map.count

/// The overview of one investigation, from its replayed state only (no hand-written summaries).
let private overviewKeys (inv: Investigation) (events: Event list) : (string * J) list =
    let l = labelsOf events
    let openUnknowns = inv.Unknowns |> Map.filter (fun _ u -> u.State = Identified || u.State = Investigating) |> Map.count
    let openContradictions = inv.Contradictions |> Map.filter (fun _ c -> c.State = Detected || c.State = Triaged || c.State = ContradictionInvestigating) |> Map.count
    [ "ovHypotheses", s (string inv.Hypotheses.Count + " (" + string (openHypotheses inv) + " open)")
      "ovEvidence", s (string inv.Evidence.Count)
      "ovContradictions", s (string inv.Contradictions.Count + " (" + string openContradictions + " open)")
      "ovUnknowns", s (string inv.Unknowns.Count + " (" + string openUnknowns + " open)")
      "ovCause", s (causeSummary inv)
      "ovVerification", s (fst (verificationSummary inv))
      "ovLastActivity",
      s (match List.tryLast events with Some e -> Words.date e.At + " · " + Words.clip 110 (describe inv l e.Body) | None -> "") ]

let private workspaceKeys (st: AppState) (inv: Investigation) (events: Event list) (real: bool) : (string * J) list =
    let l = labelsOf events
    let iid = inv.Id
    let hypotheses = inv.Hypotheses |> Map.toList |> List.map snd |> List.sortBy (fun h -> lbl l.Hyp h.Id.Value)
    let evidence = inv.Evidence |> Map.toList |> List.map snd |> List.sortBy (fun e -> e.AdmittedAt)
    let rels = inv.Relationships |> Map.toList |> List.map snd
    let causes = inv.Causes |> Map.toList |> List.map snd |> List.sortBy (fun c -> lbl l.Cause c.Id.Value)
    let corrections = inv.CorrectiveActions |> Map.toList |> List.map snd |> List.sortBy (fun c -> lbl l.Corr c.Id.Value)
    let criteria = inv.Criteria |> Map.toList |> List.map snd |> List.sortBy (fun c -> lbl l.Crit c.Id.Value)
    let actions = inv.Actions |> Map.toList |> List.map snd |> List.sortBy (fun a -> lbl l.Act a.Id.Value)
    let hypName (h: HypothesisId) =
        lbl l.Hyp h.Value + (inv.Hypotheses |> Map.tryFind h |> Option.map (fun x -> ": " + Words.clip 60 x.Statement.Value) |> Option.defaultValue "")
    let evName (e: EvidenceId) =
        lbl l.Ev e.Value + (inv.Evidence |> Map.tryFind e |> Option.map (fun x -> ": " + Words.clip 60 (evidenceText x)) |> Option.defaultValue "")
    let isOpen = Transitions.isOpen inv.Lifecycle
    // Frozen originals and earlier-format copies are read-only: no action is offered at all, not merely refused.
    let editable =
        real && st.Investigations.ContainsKey iid && formatOf st iid <> LegacyFormat && (match st.Screen with DemoScreen _ -> false | _ -> true)
    let canAct = isOpen && editable
    let observation = inv.Observations |> Map.toList |> List.map snd |> List.sortBy (fun o -> o.Provenance.RecordedAt) |> List.tryHead
    let resolution = if real then Policy.resolutionBlockers inv else []
    let remote, remoteTone = remoteText st iid
    let hypothesisItems =
        hypotheses
        |> List.map (fun h ->
            let supporting = rels |> List.filter (fun r -> r.Hypothesis = h.Id && r.Kind = Supports) |> List.map (fun r -> lbl l.Ev r.Evidence.Value)
            let contradicting = rels |> List.filter (fun r -> r.Hypothesis = h.Id && r.Kind = Contradicts) |> List.map (fun r -> lbl l.Ev r.Evidence.Value)
            let unavailable =
                Words.hypothesisCommands
                |> List.filter (fun (_, c, _) -> Transitions.hypothesis h.State c |> Option.isNone)
                |> List.map (fun (_, c, label) -> label + " (only when " + legalFrom Transitions.hypothesis Words.allHypothesisStates Words.hypothesis c + ")")
            let last = h.History |> List.tryLast
            // R6.2: posture is explained by linked evidence and open blockers, never a number or percentage.
            let linked kind =
                rels |> List.filter (fun r -> r.Hypothesis = h.Id && r.Kind = kind && (inv.Evidence |> Map.tryFind r.Evidence |> Option.exists (fun e -> e.SupersededBy.IsNone)))
                |> List.map (fun r -> lbl l.Ev r.Evidence.Value)
            let describeLinks (word: string) (labels: string list) =
                match labels with
                | [] -> "no " + word + " evidence"
                | [ one ] -> "1 " + word + " (" + one + ")"
                | many -> string many.Length + " " + word + " (" + Words.join ", " many + ")"
            let causeBlockersHere =
                if not real then []
                else
                    inv.Causes |> Map.toList |> List.map snd
                    |> List.filter (fun c -> c.Members |> List.exists (fun m -> m.Hypothesis = h.Id))
                    |> List.map (fun c -> Policy.causeBlockers inv c.Id)
                    |> List.filter (fun blockers -> not (blockers |> List.exists (fun e -> e.Code = "CAUSE.STATE")))
                    |> List.concat
            let posture =
                Words.hypothesis h.State + " because of " + describeLinks "supporting" (linked Supports) + ", " + describeLinks "contradicting" (linked Contradicts)
                + ", " + describeLinks "telling-apart" (linked Discriminates) + ", " + describeLinks "inconclusive" (linked Insufficient) + "."
                + (match causeBlockersHere with
                   | [] -> ""
                   | blockers -> " Confirming it as a cause is blocked by: " + Words.join "; " (blockers |> List.map (fun e -> e.Message)))
            item (
                [ "id", s (Id.text h.Id)
                  "href", s (ScreenRoute.format (EntityScreen(iid, HypothesisEntity, h.Id.Value)))
                  "isFocused", b (st.Screen = EntityScreen(iid, HypothesisEntity, h.Id.Value))
                  "label", s (lbl l.Hyp h.Id.Value)
                  "statement", s h.Statement.Value
                  "mechanism", s (h.Mechanism |> Option.map (fun m -> m.Value) |> Option.defaultValue "")
                  "hasMechanism", b h.Mechanism.IsSome
                  "state", s (Words.hypothesis h.State)
                  "tone", s (Words.hypothesisTone h.State)
                  "isRuledOut", b (h.State = Falsified)
                  "posture", s posture
                  "supportedBy", s (if supporting.IsEmpty then "none" else Words.join ", " supporting)
                  "contradictedBy", s (if contradicting.IsEmpty then "none" else Words.join ", " contradicting)
                  "lastChange",
                  s (
                      match last with
                      | Some c -> "Last change: " + Words.hypothesis c.From + " → " + Words.hypothesis c.To + " because: " + c.Reason.Value + (if c.Evidence.IsEmpty then "" else " (evidence " + Words.join ", " (c.Evidence |> List.map (fun e -> lbl l.Ev e.Value)) + ")")
                      | None -> "No changes yet."
                  )
                  "unavailable", s (if unavailable.IsEmpty then "" else "Not available now: " + Words.join "; " unavailable)
                  "by", s (Words.actor h.Provenance.Actor) ]
                @ (Words.hypothesisCommands
                   |> List.map (fun (name, c, _) -> "hide" + name, b (not canAct || Transitions.hypothesis h.State c |> Option.isNone)))
            ))
    let hypothesisChanges =
        hypotheses
        |> List.collect (fun h ->
            h.History
            |> List.map (fun c ->
                c.Sequence,
                item
                    [ "id", s (Id.text h.Id + "-" + string c.Sequence)
                      "text",
                      s (
                          lbl l.Hyp h.Id.Value + ": " + Words.hypothesis c.From + " → " + Words.hypothesis c.To + " — " + c.Reason.Value
                          + (if c.Evidence.IsEmpty then "" else " (evidence " + Words.join ", " (c.Evidence |> List.map (fun e -> lbl l.Ev e.Value)) + ")")
                      )
                      "at", s (Words.date c.At) ]))
        |> List.sortBy fst
        |> List.map snd
    let option (id: string) (label: string) = item [ "id", s id; "label", s label ]
    let evidenceItems =
        evidence
        |> List.map (fun e ->
            let isUrl = match e.Content with ArtifactEvidence _ -> true | _ -> false
            item
                [ "id", s (Id.text e.Id)
                  "href", s (ScreenRoute.format (EntityScreen(iid, EvidenceEntity, e.Id.Value)))
                  "isFocused", b (st.Screen = EntityScreen(iid, EvidenceEntity, e.Id.Value))
                  "label", s (lbl l.Ev e.Id.Value)
                  "content", s (evidenceText e)
                  "isUrl", b isUrl
                  "isText", b (not isUrl)
                  "url", s (match e.Content with ArtifactEvidence u -> u | _ -> "")
                  "category", s (Words.category e.Category)
                  "source", s e.Source.Value
                  "captured", s (e.CapturedAt |> Option.map Words.day |> Option.defaultValue "not given")
                  "checked", s (Words.verificationStatus e.Verification)
                  "superseded", s (match e.SupersededBy with Some r -> "Replaced by " + lbl l.Ev r.Value | None -> "")
                  "provenance", s ("Added by " + Words.actor e.Provenance.Actor + " on " + Words.date e.Provenance.RecordedAt) ])
    let relationshipItems =
        rels
        |> List.sortBy (fun r -> r.Provenance.RecordedAt, lbl l.Ev r.Evidence.Value)
        |> List.map (fun r ->
            item
                [ "id", s (Id.text r.Id)
                  "text", s (evName r.Evidence + " — " + Words.relationship r.Kind + " — " + hypName r.Hypothesis)
                  "rationale", s (r.Rationale |> Option.map (fun x -> x.Value) |> Option.defaultValue "")
                  "isContradiction", b (r.Kind = Contradicts) ])
    let contradictingLinks =
        rels
        |> List.filter (fun r -> r.Kind = Contradicts)
        |> List.map (fun r -> item [ "id", s (Id.text r.Id); "text", s ("Evidence " + evName r.Evidence + " contradicts hypothesis " + hypName r.Hypothesis) ])
    // m2: a button that needs no typed input is shown only when the kernel would
    // accept that exact command now (decide without emitting anything).
    let accepts (command: Command) =
        editable
        && (Decide.probe inv (Actor.human "local-user") st.Now command |> Result.isOk)
    let transitionFlags (table: (string * 'C * string) list) (legal: 'C -> bool) =
        table |> List.map (fun (name, c, _) -> "hide" + name, b (not canAct || not (legal c)))
    let unknownItems =
        inv.Unknowns
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun u -> u.Provenance.RecordedAt)
        |> List.map (fun u ->
            item (
                [ "id", s (Id.text u.Id)
                  "question", s u.Question.Value
                  "materiality", s (Words.materiality u.Materiality)
                  "isBlocking", b (u.Materiality = Blocking)
                  "state", s (Words.unknown u.State)
                  "basis", s (u.Basis |> Option.map (fun x -> x.Value) |> Option.defaultValue "") ]
                @ transitionFlags Words.unknownCommands (fun c -> Transitions.unknown u.State c |> Option.isSome)
            ))
    let contradictionItems =
        inv.Contradictions
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun c -> c.Provenance.RecordedAt)
        |> List.map (fun c ->
            item (
                [ "id", s (Id.text c.Id)
                  "statement", s c.Statement.Value
                  "evidence", s (if c.Evidence.IsEmpty then "none cited" else Words.join ", " (c.Evidence |> List.map evName))
                  "materiality", s (Words.materiality c.Materiality)
                  "state", s (Words.contradiction c.State)
                  "basis", s (c.Basis |> Option.map (fun x -> x.Value) |> Option.defaultValue "") ]
                @ transitionFlags Words.contradictionCommands (fun cmd -> Transitions.contradiction c.State cmd |> Option.isSome)
            ))
    let actionItems =
        actions
        |> List.map (fun a ->
            item (
                [ "id", s (Id.text a.Id)
                  "label", s (lbl l.Act a.Id.Value)
                  "kind", s (Words.actionKind a.Kind)
                  "purpose", s a.Purpose.Value
                  "targets", s (if a.Targets.IsEmpty then "no hypothesis" else Words.join ", " (a.Targets |> List.map hypName))
                  "state", s (Words.action a.State) ]
                @ transitionFlags Words.actionCommands (fun c -> accepts (TransitionAction(a.Id, c)))
            ))
    let findingItems =
        inv.Findings
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun f -> f.Provenance.RecordedAt)
        |> List.map (fun f ->
            item
                [ "id", s (Id.text f.Id)
                  "action", s (lbl l.Act f.Action.Value)
                  "result", s f.OriginalResult.Value
                  "interpretation", s (f.Interpretation |> Option.map (fun x -> x.Value) |> Option.defaultValue "") ])
    let predictionItems =
        inv.Predictions
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun p -> p.Provenance.RecordedAt)
        |> List.map (fun p ->
            item
                [ "id", s (Id.text p.Id)
                  "text", s (hypName p.Hypothesis + " predicts: " + p.Statement.Value + " (" + Words.strength p.Strength + ")")
                  "timing",
                  s (
                      match p.Timing with
                      | Prospective -> "Made before the result was known"
                      | Retrospective -> "Made after a relevant finding (retrospective: weaker test)"
                  )
                  // R4.3: checks that tell hypotheses apart are preferred.
                  "isDiscriminating", b (p.Strength = DiscriminatingPrediction) ])
    let negativeItems =
        inv.NegativeKnowledge
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun x -> x.Provenance.RecordedAt)
        |> List.map (fun x ->
            item
                [ "id", s (Id.text x.Id)
                  "statement", s x.Statement.Value
                  "scope", s x.Scope.Value
                  "basis", s (Words.join ", " (x.Basis |> List.map (fun e -> lbl l.Ev e.Value))) ])
    // Policy decides: a CAUSE.STATE blocker means the cause is not confirmable in its
    // state, so it has no confirmation work to list and no Confirm button.
    let gate = causes |> List.map (fun c -> c, (if real then Policy.causeBlockers inv c.Id else []))
    let confirmable (blockers: DomainError list) = not (blockers |> List.exists (fun e -> e.Code = "CAUSE.STATE"))
    let causeBlockers = gate |> List.map (fun (c, blockers) -> c, (if confirmable blockers then blockers else []))
    let causeItems =
        causeBlockers
        |> List.map (fun (c, blockers) ->
            item
                [ "id", s (Id.text c.Id)
                  "label", s (lbl l.Cause c.Id.Value)
                  "outcome", s c.Outcome.Value
                  "members", s (Words.join "; " (c.Members |> List.map (fun m -> Words.role m.Role + ": " + hypName m.Hypothesis)))
                  "mechanism", s (c.Mechanism |> Option.map (fun x -> x.Value) |> Option.defaultValue "not stated")
                  "state", s (Words.cause c.State)
                  "isConfirmed", b (c.State = CauseConfirmed)
                  "hideConfirm", b (not editable || not (Policy.canConfirm inv c.Id))
                  "blockerCount", n blockers.Length
                  "isBlocked", b (not blockers.IsEmpty)
                  "readiness",
                  s (
                      if c.State = CauseConfirmed then "Confirmed."
                      elif blockers.IsEmpty then "Ready to confirm: every condition is met."
                      else "Cannot be confirmed yet — " + string blockers.Length + " reason(s) listed below."
                  ) ])
    let causeBlockerItems =
        causeBlockers
        |> List.collect (fun (c, blockers) ->
            blockers
            |> List.mapi (fun i e -> item [ "id", s (Id.text c.Id + "-" + string i); "cause", s (lbl l.Cause c.Id.Value); "code", s e.Code; "message", s e.Message; "rule", s e.Rule ]))
    let correctionItems =
        corrections
        |> List.map (fun c ->
            item (
                [ "id", s (Id.text c.Id)
                  "label", s (lbl l.Corr c.Id.Value)
                  "kind", s (Words.correctionKind c.Kind)
                  "isMitigation", b (c.Kind <> Correction)
                  "description", s c.Description.Value
                  "targets", s (Words.join ", " (c.Targets |> List.map (fun t -> lbl l.Cause t.Value)))
                  "state", s (Words.correction c.State) ]
                @ transitionFlags Words.correctionCommands (fun cmd -> accepts (TransitionCorrection(c.Id, cmd)))
            ))
    // VERIFICATION.EVIDENCE_PREDATES_ROUND: only evidence admitted after verification of the
    // chosen fix started (and still active) can verify it (Policy.freshEvidence).
    let roundsInPlay =
        match Guid.TryParse(draft st "verCriterion") with
        | true, g -> inv.Criteria |> Map.tryFind (Id.create g) |> Option.map (fun c -> [ Policy.verificationRound inv c.CorrectiveAction ]) |> Option.defaultValue []
        | _ ->
            corrections
            |> List.filter (fun c -> c.State = AwaitingVerificationOfEffect)
            |> List.map (fun c -> Policy.verificationRound inv c.Id)
    let verificationEvidence = evidence |> List.filter (fun e -> roundsInPlay |> List.exists (fun r -> Policy.freshEvidence inv r e.Id))
    let verificationEvidenceNote =
        match evidence.Length - verificationEvidence.Length with
        | 0 -> ""
        | excluded ->
            string excluded + " earlier or replaced evidence item(s) are not offered: verification needs evidence added after verification of the fix started."
    let criterionItems =
        criteria
        |> List.map (fun c ->
            let verifications = inv.Verifications |> Map.toList |> List.map snd |> List.filter (fun v -> v.Criterion = c.Id)
            let met = Policy.criterionSatisfied inv c
            item
                [ "id", s (Id.text c.Id)
                  "label", s (lbl l.Crit c.Id.Value)
                  "expected", s c.ExpectedOutcome.Value
                  "correction", s (lbl l.Corr c.CorrectiveAction.Value)
                  "isMet", b met
                  "status", s (if met then "Met, with evidence" elif verifications.IsEmpty then "Not yet verified" else "Not met yet") ])
    let verificationItems =
        inv.Verifications
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun v -> v.Provenance.RecordedAt)
        |> List.map (fun v ->
            item
                [ "id", s (Id.text v.Id)
                  "text",
                  s (lbl l.Crit v.Criterion.Value + ": " + Words.outcome v.Outcome + (if v.Evidence.IsEmpty then " (no evidence cited)" else " — evidence " + Words.join ", " (v.Evidence |> List.map evName))) ])
    let conclusionItems =
        inv.Conclusions
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun c -> c.Sequence)
        |> List.map (fun c ->
            item
                [ "id", s (Id.text c.Id)
                  "statement", s c.Statement.Value
                  "strength", s (Words.conclusion c.Strength)
                  "residual", s (c.ResidualUncertainty |> Option.map (fun x -> x.Value) |> Option.defaultValue "None stated") ])
    let proposalItems =
        inv.Proposals
        |> Map.toList
        |> List.map snd
        |> List.sortBy (fun p -> p.Provenance.RecordedAt)
        |> List.map (fun p ->
            item
                [ "id", s (Id.text p.Id)
                  "text", s p.Text.Value
                  "rationale", s (p.Rationale |> Option.map (fun x -> x.Value) |> Option.defaultValue "")
                  "state", s (Words.proposal p.State)
                  "decision", s (p.Decision |> Option.map (fun x -> x.Value) |> Option.defaultValue "")
                  "isActive", b (p.State = ProposalActive && canAct)
                  "by", s (Words.actor p.Provenance.Actor)
                  "acceptLabel",
                  s (
                      match p.Kind with
                      | ProposedHypothesis -> "Accept as hypothesis"
                      | ProposedAction -> "Accept as planned check"
                      | ProposedUnknown -> "Accept as open question"
                  ) ])
    let historyItems =
        events
        |> List.rev
        |> List.map (fun e ->
            item
                [ "id", s (string e.Sequence)
                  "sequence", n (int e.Sequence)
                  "at", s (Words.date e.At)
                  "actor", s (Words.actor e.Actor)
                  "text", s (describe inv l e.Body) ])
    // R8.4 outstanding work: only what Policy, the transition tables and the
    // recorded verifications already say; no legality is decided here.
    let outstanding =
        let fromPolicy source (errors: DomainError list) = errors |> List.map (fun e -> source, e.Message, e.Code)
        let unknownWork =
            inv.Unknowns |> Map.toList |> List.map snd
            |> List.filter (fun u -> Transitions.unknown u.State ResolveUnknown |> Option.isSome)
            |> List.map (fun u -> "Open question", "Answer or accept: \"" + u.Question.Value + "\" (" + Words.materiality u.Materiality + ")", "UNKNOWN.OPEN")
        let contradictionWork =
            inv.Contradictions |> Map.toList |> List.map snd
            |> List.filter (fun c -> Transitions.contradiction c.State ResolveContradiction |> Option.isSome)
            |> List.map (fun c -> "Contradiction", "Explain or accept: \"" + c.Statement.Value + "\" (" + Words.materiality c.Materiality + ")", "CONTRADICTION.OPEN")
        let criterionWork =
            criteria
            |> List.filter (fun c -> not (Policy.criterionSatisfied inv c))
            |> List.map (fun c -> "Verification", "Verify with evidence: " + lbl l.Crit c.Id.Value + " \"" + c.ExpectedOutcome.Value + "\"", "CRITERION.UNVERIFIED")
        let causeWork =
            causeBlockers |> List.collect (fun (c, blockers) -> fromPolicy ("Cause " + lbl l.Cause c.Id.Value) blockers)
        let resolveWork = if isOpen then fromPolicy "Resolution" resolution else []
        unknownWork @ contradictionWork @ criterionWork @ causeWork @ resolveWork
        |> List.distinctBy (fun (_, message, _) -> message)
        |> List.mapi (fun i (source, message, code) -> item [ "id", s (string i); "source", s source; "message", s message; "code", s code ])
    let resolutionItems = resolution |> List.mapi (fun i e -> item [ "id", s (string i); "code", s e.Code; "message", s e.Message; "rule", s e.Rule ])
    let lifecycleFlags =
        LifecycleCommands.all
        |> List.map (fun (name, c, _) ->
            match c with
            // Reopening needs a typed basis: structural table plus the refusal explanation.
            | ReopenInvestigation _ -> "lifeHide_" + name, b (not editable || not (LifecycleCommands.legal inv.Lifecycle c))
            | _ -> "lifeHide_" + name, b (not (accepts c)))
    [ "wsId", s (Id.text iid)
      "wsTitle", s inv.Title.Value
      "wsObservation", s (observation |> Option.map (fun o -> o.Original.Value) |> Option.defaultValue "")
      "wsObservationBy", s (observation |> Option.map (fun o -> "Recorded by " + Words.actor o.Provenance.Actor + " on " + Words.date o.Provenance.RecordedAt) |> Option.defaultValue "")
      "wsLifecycle", s (Words.lifecycle inv.Lifecycle)
      "wsVersion", n (int inv.Version)
      "wsIsOpen", b isOpen
      "wsIsClosed", b (not isOpen)
      "reportHref", s (ScreenRoute.format (ReportScreen iid))
      "workspaceHref", s (ScreenRoute.format (WorkspaceScreen iid))
      "localStatus", s (localText st iid)
      "remoteStatus", s remote
      "remoteTone", s remoteTone
      "hypotheses", JArr hypothesisItems
      "hasHypotheses", b (not hypothesisItems.IsEmpty)
      "hypothesisChanges", JArr hypothesisChanges
      "hypothesisOptions", JArr(hypotheses |> List.map (fun h -> option (Id.text h.Id) (hypName h.Id + " (" + Words.hypothesis h.State + ")")))
      "evidence", JArr evidenceItems
      "hasEvidence", b (not evidenceItems.IsEmpty)
      "evidenceOptions", JArr(evidence |> List.map (fun e -> option (Id.text e.Id) (evName e.Id)))
      "verEvidenceOptions", JArr(verificationEvidence |> List.map (fun e -> option (Id.text e.Id) (evName e.Id)))
      "verEvidenceNote", s verificationEvidenceNote
      "relationships", JArr relationshipItems
      "contradictingLinks", JArr contradictingLinks
      "hasContradictingLinks", b (not contradictingLinks.IsEmpty)
      "contradictions", JArr contradictionItems
      "unknowns", JArr unknownItems
      "hasUnknowns", b (not unknownItems.IsEmpty)
      "predictions", JArr predictionItems
      "actions", JArr actionItems
      "actionOptions", JArr(actions |> List.map (fun a -> option (Id.text a.Id) (lbl l.Act a.Id.Value + ": " + Words.clip 60 a.Purpose.Value)))
      "findings", JArr findingItems
      "negative", JArr negativeItems
      "causes", JArr causeItems
      "hasCauses", b (not causeItems.IsEmpty)
      "causeBlockers", JArr causeBlockerItems
      "causeOptions", JArr(causes |> List.map (fun c -> option (Id.text c.Id) (lbl l.Cause c.Id.Value + ": " + Words.clip 60 c.Outcome.Value + " (" + Words.cause c.State + ")")))
      "corrections", JArr correctionItems
      "correctionOptions", JArr(corrections |> List.map (fun c -> option (Id.text c.Id) (lbl l.Corr c.Id.Value + ": " + Words.clip 60 c.Description.Value)))
      "criteria", JArr criterionItems
      "criterionOptions", JArr(criteria |> List.map (fun c -> option (Id.text c.Id) (lbl l.Crit c.Id.Value + ": " + Words.clip 60 c.ExpectedOutcome.Value)))
      "verifications", JArr verificationItems
      "conclusions", JArr conclusionItems
      "proposals", JArr proposalItems
      "hasProposals", b (not proposalItems.IsEmpty)
      "history", JArr historyItems
      "resolutionBlockers", JArr resolutionItems
      "challengeAvailable", b (real && isOpen && Challenge.available inv)
      "challengeHidden", b (not (real && isOpen && Challenge.available inv))
      "challengeStatus",
      s (
          match Challenge.suggestion inv with
          | Some _ when Challenge.available inv -> "A supported explanation still has alternatives standing: ask what would prove it wrong."
          | Some _ -> "The suggested falsifying check is waiting for your decision below."
          | None -> "Nothing to challenge yet: it applies once one explanation is supported while others still stand."
      )
      "challengeQuestions",
      JArr(
          causeBlockers
          |> List.collect (fun (c, blockers) -> blockers |> List.mapi (fun i e -> item [ "id", s (Id.text c.Id + "-q" + string i); "text", s (Challenge.question e) ]))
      )
      "outstanding", JArr outstanding
      "hasOutstanding", b (not outstanding.IsEmpty)
      "noOutstanding", b outstanding.IsEmpty
      "outstandingSummary", s (if outstanding.IsEmpty then "Nothing outstanding." else string outstanding.Length + " thing(s) still need doing.")
      "canResolve", b (resolution.IsEmpty && LifecycleCommands.legal inv.Lifecycle ResolveInvestigation)
      "resolveSummary",
      s (
          if inv.Lifecycle = Resolved || inv.Lifecycle = Closed then "This investigation is " + Words.lifecycle inv.Lifecycle + "."
          elif resolution.IsEmpty then "Ready to resolve: every condition is met."
          else "Cannot be resolved yet — " + string resolution.Length + " thing(s) still need doing:"
      ) ]
    @ lifecycleFlags
    @ syncKeys st iid
    @ overviewKeys inv events

let private reportKeys (inv: Investigation) (events: Event list) (real: bool) : (string * J) list =
    match real with
    | false ->
        [ "reportTitle", s inv.Title.Value
          "reportKind", s "Interim report"
          "reportIsFinal", b false
          "reportIsInterim", b true
          "reportDigest", s ""
          "reportVersion", n (int inv.Version)
          "reportObservation", s ""
          "reportRows", JArr []
          "reportUnresolved", JArr []
          "hasUnresolved", b false ]
    | true ->
        let model = Report.build inv events
        let rows =
            model.Sections
            |> List.collect (fun section ->
                match section.Items with
                | [] -> [ item [ "id", s (section.Id + "-empty"); "section", s section.Title; "first", b true; "heading", s "Nothing recorded."; "detail", s ""; "status", s "" ] ]
                | items ->
                    items
                    |> List.mapi (fun i it ->
                        item [ "id", s (section.Id + "-" + string i); "section", s section.Title; "first", b (i = 0); "heading", s it.Heading; "detail", s it.Detail; "status", s it.Status ]))
        let unresolved = model.UnresolvedWork |> List.mapi (fun i it -> item [ "id", s (string i); "heading", s it.Heading; "detail", s it.Detail; "status", s it.Status ])
        let final = model.Kind = "Final"
        [ "reportTitle", s model.Title
          "reportKind", s (if final then "Final report" else "Interim report — work remains unresolved")
          "reportIsFinal", b final
          "reportIsInterim", b (not final)
          "reportDigest", s model.SnapshotDigest
          "reportVersion", n (int model.Version)
          "reportObservation", s model.Observation
          "reportRows", JArr rows
          "reportUnresolved", JArr unresolved
          "hasUnresolved", b (not unresolved.IsEmpty) ]

let private demoKeys (st: AppState) : (string * J) list =
    let rows =
        match st.Demo with
        | DemoReady d ->
            d.Valid
            |> List.sortBy (fun v -> v.Loaded.State.Title.Value)
            |> List.map (fun v ->
                let inv = v.Loaded.State
                let iid = inv.Id
                let events = v.Loaded.Events
                let last = List.last events
                let l = labelsOf events
                let first = inv.Observations |> Map.toList |> List.map snd |> List.sortBy (fun o -> o.Provenance.RecordedAt) |> List.tryHead
                let verification, tone = verificationSummary inv
                let openUnknowns = inv.Unknowns |> Map.filter (fun _ u -> u.State = Identified || u.State = Investigating) |> Map.count
                item
                    [ "id", s (Id.text iid)
                      "key", s v.ScenarioKey
                      "title", s inv.Title.Value
                      "lifecycle", s (Words.lifecycle inv.Lifecycle)
                      "problem", s (first |> Option.map (fun o -> Words.clip 180 o.Original.Value) |> Option.defaultValue "")
                      "hypotheses", n inv.Hypotheses.Count
                      "openHypotheses", n (openHypotheses inv)
                      "evidenceCount", n inv.Evidence.Count
                      "contradictions", n inv.Contradictions.Count
                      "unknowns", n openUnknowns
                      "cause", s (causeSummary inv)
                      "verification", s verification
                      "verificationTone", s tone
                      "lastActivity", s (Words.date last.At + " · " + Words.clip 110 (describe inv l last.Body))
                      "version", n (int inv.Version)
                      "href", s (ScreenRoute.format (WorkspaceScreen iid))
                      "frozenHref", s (ScreenRoute.format (DemoScreen iid))
                      "hasWorkingCopy", b (st.Investigations.ContainsKey iid)
                      "noWorkingCopy", b (not (st.Investigations.ContainsKey iid))
                      "stateDigest", s v.Loaded.StateSha256 ])
        | _ -> []
    let rejected =
        match st.Demo with
        | DemoReady d ->
            d.Rejected |> List.map (fun r -> item [ "id", s r.DirectoryId; "key", s r.ScenarioKey; "code", s r.Code; "detail", s (Words.clip 240 r.Detail) ])
        | _ -> []
    [ "demoInvestigations", JArr rows
      "hasDemo", b (not rows.IsEmpty)
      "demoLoading", b (st.Demo = DemoLoading)
      "demoFailed", b (match st.Demo with DemoFailed _ -> true | _ -> false)
      "demoFailure", s (match st.Demo with DemoFailed r -> r | _ -> "")
      "demoEmpty", b (match st.Demo with DemoReady d -> d.Valid.IsEmpty | _ -> false)
      "demoRejected", JArr rejected
      "hasDemoRejected", b (not rejected.IsEmpty)
      "demoSummary",
      s (
          match st.Demo with
          | DemoReady d ->
              string d.Valid.Length + " investigation(s) from " + CausaData.Pins.tag + ", each replayed from its event history and verified against the Causa storage contract v1.0.0"
              + (if d.Rejected.IsEmpty then "." else "; " + string d.Rejected.Length + " refused (see below).")
          | DemoLoading -> "Loading the frozen demonstration data…"
          | DemoFailed _ -> "The demonstration data could not be loaded."
      ) ]

let private homeKeys (st: AppState) : (string * J) list =
    let rows =
        st.Investigations
        |> Map.toList
        |> List.choose (fun (iid, events) ->
            match Evolve.replay events, List.tryLast events with
            | Some inv, Some last ->
                Some(
                    last.At,
                    item
                        [ "id", s (Id.text iid)
                          "title", s inv.Title.Value
                          "status", s (Words.lifecycle inv.Lifecycle)
                          "updated", s ("Updated " + Words.date last.At)
                          "href", s (ScreenRoute.format (WorkspaceScreen iid))
                          "reportHref", s (ScreenRoute.format (ReportScreen iid))
                          "sync", s (fst (remoteText st iid)) ]
                )
            | _ -> None)
        |> List.sortByDescending fst
        |> List.map snd
    let unreadable =
        st.StoredIndex
        |> Map.toList
        |> List.filter (fun (iid, _) -> st.Loaded && not (st.Investigations.ContainsKey iid))
        |> List.map (fun (iid, e) ->
            item
                [ "id", s (Id.text iid)
                  "title", s e.Title
                  "reason", s ("Could not be read: " + (Map.tryFind iid st.Unreadable |> Option.defaultValue "not loaded") + ". It is kept in the index and was not changed.") ])
    demoKeys st
    @ [ "unreadable", JArr unreadable
        "hasUnreadable", b (not unreadable.IsEmpty)
        "investigations", JArr rows; "hasInvestigations", b (not rows.IsEmpty); "noInvestigations", b rows.IsEmpty ]

/// The complete ViewState: every key in every projection (Limen rule).
let private factoryKeys (st: AppState) : (string * J) list =
    let info = match st.Factory with FactoryLoaded f -> Some f | _ -> None
    let status =
        match st.Factory with
        | FactoryNotRequested | FactoryLoading -> "Loading the factory record…"
        | FactoryLoaded f -> f.Label + " — commit " + (if f.Commit.Length > 7 then f.Commit.Substring(0, 7) else f.Commit) + ", built " + f.BuiltAt
        | FactoryFailed reason -> "The factory record could not be loaded: " + reason
    let requirements = info |> Option.map (fun f -> f.Requirements) |> Option.defaultValue []
    let workItems = info |> Option.map (fun f -> f.WorkItems) |> Option.defaultValue []
    let statusOf slice = workItems |> List.tryFind (fun (id, _, _) -> id = slice) |> Option.map (fun (_, _, st) -> st) |> Option.defaultValue "not in the work queue"
    let slices =
        requirements
        |> List.groupBy (fun r -> r.ReqSlice)
        |> List.map (fun (slice, reqs) ->
            item [ "id", s (if slice = "" then "unassigned" else slice); "slice", s (if slice = "" then "No slice" else slice); "count", n reqs.Length; "status", s (statusOf slice) ])
    [ "isFactory", b (st.Screen = FactoryScreen)
      "factoryStatus", s status
      "hasFactory", b info.IsSome
      "factoryCommit", s (info |> Option.map (fun f -> f.Commit) |> Option.defaultValue "")
      "factoryExperiment", s (info |> Option.map (fun f -> f.Experiment) |> Option.defaultValue "")
      "factoryRequirementCount", n requirements.Length
      "factoryTests",
      s (
          match info |> Option.bind (fun f -> f.Tests) with
          | Some t ->
              string t.Total + " tests recorded, " + string t.Passed + " passed, " + string t.Failed + " failed"
              + (if t.RunCommit <> "" then " (run on commit " + (if t.RunCommit.Length > 7 then t.RunCommit.Substring(0, 7) else t.RunCommit) + ")" else "")
              + ". Requirements: " + string t.Tested + " tested, " + string t.Untested + " untested, " + string t.Failing + " failing."
              + (if t.Suites = "engine" then " Covers the engine (xUnit) tests only; the browser end-to-end suite runs in CI and in the deploy acceptance job and is not included here." else "")
          | None -> "Test results were not available when this site was built."
      )
      "factoryBuildStatus",
      s (
          match info |> Option.map (fun f -> f.BuildStatus) with
          | Some "passed" -> "Build status: every recorded test passed (see the suites covered above)"
          | Some "failing" -> "Build status: some recorded tests failed"
          | _ -> "Build status: unknown (no test run was recorded for this build)"
      )
      "factoryRequirements",
      JArr(
          requirements
          |> List.map (fun r ->
              item
                  [ "id", s r.ReqId
                    "title", s r.ReqTitle
                    "scope", s (if r.ReqScope = "scoped-factory" then "Factory" else "Product")
                    "slice", s (if r.ReqSlice = "" then "—" else r.ReqSlice)
                    "status", s (statusOf r.ReqSlice)
                    "coverage", s (if r.Coverage = "" then "no test run recorded" else r.Coverage) ])
      )
      "factorySlices", JArr slices
      "factoryWorkItems", JArr(workItems |> List.map (fun (id, title, status) -> item [ "id", s id; "title", s title; "status", s status ])) ]

/// The addressed entity of an EntityScreen: (kind word, label, text, state).
let private focusOf (inv: Investigation) (events: Event list) (kind: EntityKind) (g: Guid) : (string * string * string * string) option =
    let l = labelsOf events
    match kind with
    | HypothesisEntity -> inv.Hypotheses |> Map.tryFind (Id.create g) |> Option.map (fun h -> "Hypothesis", lbl l.Hyp g, h.Statement.Value, Words.hypothesis h.State)
    | EvidenceEntity -> inv.Evidence |> Map.tryFind (Id.create g) |> Option.map (fun e -> "Evidence", lbl l.Ev g, evidenceText e, "Source: " + e.Source.Value)
    | UnknownEntity -> inv.Unknowns |> Map.tryFind (Id.create g) |> Option.map (fun u -> "Open question", "", u.Question.Value, Words.unknown u.State)
    | ContradictionEntity -> inv.Contradictions |> Map.tryFind (Id.create g) |> Option.map (fun c -> "Contradiction", "", c.Statement.Value, Words.contradiction c.State)
    | ActionEntity -> inv.Actions |> Map.tryFind (Id.create g) |> Option.map (fun a -> "Test or experiment", lbl l.Act g, a.Purpose.Value, Words.action a.State)
    | CausalAssertionEntity -> inv.Causes |> Map.tryFind (Id.create g) |> Option.map (fun c -> "Cause", lbl l.Cause g, c.Outcome.Value, Words.cause c.State)
    | CorrectiveActionEntity -> inv.CorrectiveActions |> Map.tryFind (Id.create g) |> Option.map (fun c -> "Fix", lbl l.Corr g, c.Description.Value, Words.correction c.State)
    | CriterionEntity -> inv.Criteria |> Map.tryFind (Id.create g) |> Option.map (fun c -> "Success criterion", lbl l.Crit g, c.ExpectedOutcome.Value, "")
    | ProposalEntity -> inv.Proposals |> Map.tryFind (Id.create g) |> Option.map (fun p -> "AI suggestion", "", p.Text.Value, Words.proposal p.State)
    | _ -> None

let project (st: AppState) : (string * J) list =
    let screenOk iid = (sourceOf st iid false).IsSome
    let loading =
        match st.Screen with
        | WorkspaceScreen iid | ReportScreen iid | EntityScreen(iid, _, _) -> not st.Loaded && not (screenOk iid)
        | DemoScreen iid -> not st.Loaded && (demoOf st iid).IsNone
        | _ -> not st.Loaded
    let sourced = currentId st |> Option.bind (fun iid -> sourceOf st iid (st.Screen = DemoScreen iid))
    let current = sourced |> Option.map (fun (inv, events, _) -> inv, events)
    let focus =
        match st.Screen, current with
        | EntityScreen(_, kind, g), Some(inv, events) -> focusOf inv events kind g
        | _ -> None
    let isWorkspace, isReport, missing =
        match st.Screen with
        | WorkspaceScreen iid -> screenOk iid, false, st.Loaded && not (screenOk iid)
        | EntityScreen(iid, _, _) -> screenOk iid && focus.IsSome, false, (st.Loaded && not (screenOk iid)) || (screenOk iid && focus.IsNone)
        | ReportScreen iid -> false, screenOk iid, st.Loaded && not (screenOk iid)
        | DemoScreen iid -> (demoOf st iid).IsSome, false, st.Loaded && (demoOf st iid).IsNone
        | MissingScreen _ | RefusedLinkScreen _ -> false, false, true
        | HomeScreen | SettingsScreen | FactoryScreen -> false, false, false
    let ws =
        match current, placeholder with
        | Some(inv, events), _ -> workspaceKeys st inv events true @ reportKeys inv events true
        | None, Some p -> workspaceKeys st p [] false @ reportKeys p [] false
        | None, None -> []
    let sourceKeys =
        let iid = currentId st
        let frozen, working, legacy, origin =
            match sourced, iid with
            | Some(_, _, FrozenSource d), _ -> Some d, false, false, None
            | Some(_, _, WorkingSource), Some i ->
                None, true, (formatOf st i = LegacyFormat), (match formatOf st i with CausaFormat o -> o | LegacyFormat -> None)
            | _ -> None, false, false, None
        let idText = iid |> Option.map Id.text |> Option.defaultValue ""
        let demoOfCurrent = iid |> Option.bind (demoOf st)
        let loaded = match frozen, demoOfCurrent with Some d, _ -> Some d.Loaded | None, Some d when not working -> Some d.Loaded | _ -> None
        let conflict = iid |> Option.bind (fun i -> Map.tryFind i st.SaveConflicts)
        [ "isFrozen", b frozen.IsSome
          "isWorkingCopy", b (working && not legacy)
          "isLegacy", b legacy
          "canEdit", b (working && not legacy && st.Screen <> (iid |> Option.map DemoScreen |> Option.defaultValue HomeScreen))
          "readOnly", b (frozen.IsSome || legacy)
          // A frozen original is never saved anywhere: no device or GitHub status is shown for it.
          "showPersistence", b frozen.IsNone
          "frozenBanner",
          s (
              match frozen with
              | Some d ->
                  "Frozen demonstration data from " + CausaData.Pins.tag + " (scenario \"" + d.ScenarioKey + "\"). It is read-only and never changes. Create a working copy to investigate further; the copy keeps this exact history and is saved on this device."
              | None when legacy -> "Saved before Causa storage in a format that is kept unchanged (not migrated), so it is read-only."
              | None -> ""
          )
          "createCopyKey", s idText
          "showCreateCopy", b (frozen.IsSome && not (iid |> Option.exists st.Investigations.ContainsKey))
          "hasWorkingCopy", b (frozen.IsSome && (iid |> Option.exists st.Investigations.ContainsKey))
          "workingCopyHref", s (iid |> Option.map (WorkspaceScreen >> ScreenRoute.format) |> Option.defaultValue "#/")
          "hasFrozenOriginal", b (working && demoOfCurrent.IsSome)
          "frozenOriginalHref", s (iid |> Option.map (DemoScreen >> ScreenRoute.format) |> Option.defaultValue "#/")
          "originText",
          s (
              match origin with
              | Some o -> "Working copy of frozen demonstration " + o + ". Its history starts with the frozen events, byte for byte."
              | None when working && not legacy -> "Saved on this device as a Causa storage v1.0.0 file set."
              | None -> ""
          )
          "contractText",
          s (
              match loaded with
              | Some l ->
                  "Verified on load against the Causa storage contract v1.0.0: all 17 reader stages, admissibility re-decided by this engine, snapshot equal to the replay. State digest "
                  + l.StateSha256.Substring(0, 12) + "…, events digest " + l.EventsSha256.Substring(0, 12) + "…."
              | None when working && not legacy ->
                  "Each change is decided by the engine, then the complete Causa v1.0.0 file set is rebuilt and re-verified before it is saved."
              | None -> ""
          )
          "hasSaveConflict", b conflict.IsSome
          "saveConflict", s (conflict |> Option.defaultValue "") ]
    let title =
        match st.Screen, current with
        | HomeScreen, _ -> "Causa · Root-Cause Investigation"
        | SettingsScreen, _ -> "GitHub settings · Causa"
        | WorkspaceScreen _, Some(inv, _) -> inv.Title.Value + " · Causa"
        | EntityScreen _, Some(inv, _) when isWorkspace -> inv.Title.Value + " · Causa"
        | FactoryScreen, _ -> "How this app was built · Causa"
        | ReportScreen _, Some(inv, _) -> "Report: " + inv.Title.Value + " · Causa"
        | DemoScreen _, Some(inv, _) -> inv.Title.Value + " (frozen original) · Causa"
        | _, _ when loading -> "Loading · Causa"
        | _ -> "Not found · Causa"
    let missingText =
        match st.Screen with
        | MissingScreen path -> "There is no page at \"" + Words.clip 80 path + "\"."
        | RefusedLinkScreen parameter ->
            "This link was not opened: it carries the parameter \"" + parameter + "\", which is never allowed in links because it could expose a secret."
        | EntityScreen(iid, _, _) when screenOk iid ->
            "This investigation has no such item. It may have been mistyped; history is never deleted, so nothing was removed."
        | WorkspaceScreen _ | ReportScreen _ | EntityScreen _ -> "This investigation is not on this device. It may have been created in another browser, or the link is wrong."
        | _ -> ""
    let backToInvestigation =
        match st.Screen with
        | EntityScreen(iid, _, _) when screenOk iid -> Some(ScreenRoute.format (WorkspaceScreen iid))
        | _ -> None
    let focusKeys =
        let kind, label, text, state = focus |> Option.defaultValue ("", "", "", "")
        [ "hasFocus", b focus.IsSome
          "focusKind", s kind
          "focusLabel", s label
          "focusText", s text
          "focusState", s state
          "hasMissingBack", b backToInvestigation.IsSome
          "missingBackHref", s (backToInvestigation |> Option.defaultValue "#/") ]
    let refusals =
        st.Refusals
        |> List.mapi (fun i r -> item [ "id", s (string i); "code", s r.Code; "message", s r.Message; "rule", s r.Rule; "fault", s ("Aegis fault " + r.FaultCode + " (domain refusal, " + r.Operation + ")") ])
    let draftValue f =
        match f, st.Remote with
        | "setOwner", Some r when not (st.Drafts.ContainsKey f) -> r.Owner
        | "setRepo", Some r when not (st.Drafts.ContainsKey f) -> r.Repository
        | "setBranch", Some r when not (st.Drafts.ContainsKey f) -> r.Branch
        | "setBranch", None when not (st.Drafts.ContainsKey f) -> "main"
        | "evKind", _ when not (st.Drafts.ContainsKey f) -> "text"
        | _ -> draft st f
    [ "pageTitle", s title
      "booted", b true
      "isHome", b (st.Screen = HomeScreen)
      "isWorkspace", b isWorkspace
      "isReport", b isReport
      "isSettings", b (st.Screen = SettingsScreen)
      "isMissing", b (missing && not loading)
      "isLoading", b (loading && st.Screen <> SettingsScreen)
      "missingText", s missingText
      "notice", s st.Notice
      "refusals", JArr refusals
      "hasRefusals", b (not refusals.IsEmpty)
      "connection", s (if st.Online then "Online" else "Offline — work continues on this device")
      "isOffline", b (not st.Online)
      "remoteConfigured", b st.Remote.IsSome
      "settingsRunId", s (st.Remote |> Option.map (fun r -> r.RunId) |> Option.defaultValue "")
      "settingsWorkspacePath", s (st.Remote |> Option.map (fun r -> "workspaces/demo/" + r.RunId + "/investigations/<investigation id>/") |> Option.defaultValue "")
      "canRecover", b (st.Remote |> Option.exists (fun r -> r.Token <> ""))
      "needsToken", b (st.Remote.IsSome && not (hasToken st))
      "startDisabled", b (not st.Loaded)
      "settingsSummary",
      s (
          match st.Remote with
          | Some r when r.Token = "" ->
              "GitHub repository " + r.Owner + "/" + r.Repository + " (branch " + r.Branch + ", run " + r.RunId + "). No access token in this session: enter it to publish. Tokens are kept in memory only and are asked for again after a reload."
          | Some r -> "Publishing working copies to GitHub repository " + r.Owner + "/" + r.Repository + " (branch " + r.Branch + ", run " + r.RunId + "), one commit per change, each confirmed by re-reading the commit. The access token is kept in memory for this session only; it is never saved, shown or put in links."
          | None -> "Not connected to GitHub. Investigations are saved on this device only."
      ) ]
    @ homeKeys st
    @ sourceKeys
    @ (draftFields |> List.map (fun f -> "draft_" + f, s (draftValue f)))
    @ ws
    @ focusKeys
    @ factoryKeys st

// ---------------------------------------------------------------------------
// handle: the whole engine, one message at a time
// ---------------------------------------------------------------------------

let private step (message: Inbound) (st: AppState) : AppState * Effect list * HandshakeReply =
    match message with
    | Initialize(hash, baseUrl, offer) ->
        let reply = Wire.negotiate offer
        let lifecycle = match reply with Accepted(_, _, selected) -> not selected.IsEmpty | _ -> false
        let st = { st with Screen = ScreenRoute.parse hash; LifecycleNegotiated = lifecycle; BaseUrl = baseUrl }
        let c1, st = Ids.correlation st LoadSettings
        let c3, st = Ids.correlation st LoadIndex
        let c4, st = Ids.correlation st LoadDataset
        let loads =
            [ StorageGet(c1, Storage.settingsKey); StorageGet(c3, Storage.indexKey)
              HttpCall(c4, "GET", "./data/" + CausaData.Pins.tag + ".bundle.json", [], None) ]
        let st, subscribe =
            if lifecycle then
                let c, st = Ids.correlation st Subscribe
                st, [ SubscribeConnectivity c ]
            else st, []
        let st, factory = ensureFactory st
        st, loads @ subscribe @ factory, reply
    | UiEvent(name, key, value) ->
        let st, fx = onEvent name key value st
        st, fx, NoHandshake
    | StorageDone(c, result) ->
        match Map.tryFind c st.Pending with
        | Some(KeepMarker(iid, identity, flight, resume)) ->
            let st = { st with Pending = st.Pending |> Map.remove c }
            if st.Flight <> Some flight then st, [], NoHandshake // an abandoned program: its answers are dropped
            elif identity <> currentIdentity st then abandon st, [], NoHandshake
            else
                let reply = match result with Ok _ -> CausaGitHub.Answered(200, "", Map.empty) | Error reason -> CausaGitHub.NotSent reason
                let st, fx = advance iid identity flight (resume.Resume reply) st
                st, fx, NoHandshake
        | Some op ->
            let st, fx = onStorage op result { st with Pending = st.Pending |> Map.remove c }
            st, fx, NoHandshake
        | None -> st, [], NoHandshake
    | HttpDone(c, outcome) ->
        match Map.tryFind c st.Pending with
        | Some(RemoteStep(iid, identity, flight, resume)) ->
            let st = { st with Pending = st.Pending |> Map.remove c }
            if st.Flight <> Some flight then st, [], NoHandshake // an abandoned program: its answers are dropped
            elif identity <> currentIdentity st then abandon st, [], NoHandshake
            else
                let st, fx = advance iid identity flight (resume.Resume (replyOf outcome)) st
                st, fx, NoHandshake
        | Some LoadDataset ->
            let demo =
                match outcome with
                | HttpSuccess(200, body, _) ->
                    match CausaData.decodeBundle body with
                    | Ok(tag, _) when tag <> CausaData.Pins.tag -> DemoFailed("the data bundle is " + tag + ", not " + CausaData.Pins.tag)
                    | Ok(_, files) -> (match CausaData.loadDataset files with Ok d -> DemoReady d | Error e -> DemoFailed(CausaData.describe e))
                    | Error e -> DemoFailed(CausaData.describe e)
                | HttpSuccess(status, _, _) -> DemoFailed("the server answered " + string status)
                | HttpFailure(reason, _) -> DemoFailed("the request failed (" + reason + ")")
                | HttpCancelled -> DemoFailed "the request was cancelled"
                | HttpOutcomeUnknown reason -> DemoFailed reason
            let st = adoptDeferred { st with Pending = st.Pending |> Map.remove c; Demo = demo }
            let st, fx = finishLoading st
            st, fx, NoHandshake
        | Some FactoryLoad ->
            let factory =
                match outcome with
                | HttpSuccess(200, body, _) -> (match decodeFactory body with Ok f -> FactoryLoaded f | Error e -> FactoryFailed e)
                | HttpSuccess(status, _, _) -> FactoryFailed("the server answered " + string status)
                | HttpFailure(reason, _) -> FactoryFailed("the request failed (" + reason + ")")
                | HttpCancelled -> FactoryFailed "the request was cancelled"
                | HttpOutcomeUnknown reason -> FactoryFailed reason
            { st with Pending = st.Pending |> Map.remove c; Factory = factory }, [], NoHandshake
        | _ -> { st with Pending = st.Pending |> Map.remove c }, [], NoHandshake
    | CapabilityDone(c, online) ->
        let st = { st with Pending = st.Pending |> Map.remove c; Online = Option.defaultValue st.Online online }
        let st, fx = if st.Loaded then syncNext st else st, []
        st, fx, NoHandshake
    | NavigationDone c -> { st with Pending = st.Pending |> Map.remove c }, [], NoHandshake
    | ClipboardDone(c, failure) ->
        let notice =
            match Map.tryFind c st.Pending, failure with
            | Some(CopyLink url), None -> "Link copied: " + url
            | Some(CopyLink url), Some "denied" -> "The browser did not allow copying. The link is: " + url
            | Some(CopyLink url), Some _ -> "Copying is not available here. The link is: " + url
            | _ -> st.Notice
        { st with Pending = st.Pending |> Map.remove c; Notice = notice }, [], NoHandshake
    | LocationChanged hash ->
        let st, fx = ensureFactory { st with Screen = ScreenRoute.parse hash; Refusals = []; Notice = "" }
        st, fx, NoHandshake
    | ConnectivityChanged online ->
        // Coming back online is a sync trigger (CAUSA-PROV-20 §5).
        let st = { st with Online = online; Attempted = (if online then Set.empty else st.Attempted) }
        let st, fx = if online && st.Loaded then syncNext st else st, []
        st, fx, NoHandshake
    | Unrecognized _ -> st, [], NoHandshake

/// Pure: one Limen message in, the next state and the response JSON out.
let handle (state: AppState) (messageJson: string) : AppState * string =
    let next, effects, reply = step (Wire.decode messageJson) state
    next, Wire.encode (project next) effects reply
