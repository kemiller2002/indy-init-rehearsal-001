module App.Engine.Tests.CodecTests

open System
open System.Text.Json.Nodes
open Xunit
open IndyInitRootCauseInvestigator.Engine

/// A complete investigation built through Decide.execute that emits every EventBody case.
/// Shared by the codec, sync and report tests.
module Scenario =
    let g (n: int) = Guid(sprintf "%08x-0000-4000-8000-00000000c0de" n)
    let iid: InvestigationId = Id.create (g 1)
    let human = Actor.human "kim"
    let agent = Actor.agent "assistant-1" "anthropic" "claude-opus-5-5"
    let baseTime = DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.FromHours -4.0)

    /// Text that must survive persistence byte-for-byte.
    let tricky = "Pump P-7 tripped at 03:14 — \"overcurrent\"\nOperator: température élevée ✓ 🔥 back\\slash\ttab <b>&amp;</b>"

    let obs0: ObservationId = Id.create (g 10)
    let obs1: ObservationId = Id.create (g 11)
    let h1: HypothesisId = Id.create (g 20)
    let h2: HypothesisId = Id.create (g 21)
    let h3: HypothesisId = Id.create (g 22)
    let hAccepted = g 23
    let e1: EvidenceId = Id.create (g 30)
    let e2: EvidenceId = Id.create (g 31)
    let e3: EvidenceId = Id.create (g 32)
    let e4: EvidenceId = Id.create (g 33)
    let e5: EvidenceId = Id.create (g 34)
    let r1: RelationshipId = Id.create (g 40)
    let r2: RelationshipId = Id.create (g 41)
    let r3: RelationshipId = Id.create (g 42)
    let u1: UnknownId = Id.create (g 50)
    let a1: AssumptionId = Id.create (g 51)
    let c1: ContradictionId = Id.create (g 52)
    let p1: PredictionId = Id.create (g 60)
    let x1: ActionId = Id.create (g 61)
    let f1: FindingId = Id.create (g 62)
    let n1: NegativeKnowledgeId = Id.create (g 63)
    let k1: CausalAssertionId = Id.create (g 70)
    let m1: CorrectiveActionId = Id.create (g 71)
    let cr1: CorrectiveActionId = Id.create (g 72)
    let q1: CriterionId = Id.create (g 73)
    let v1: VerificationId = Id.create (g 74)
    let con1: ConclusionId = Id.create (g 75)
    let pr1: ProposalId = Id.create (g 80)
    let pr2: ProposalId = Id.create (g 81)

    let private interimSteps: (Actor * Command) list =
        [ human, StartInvestigation(iid, "Pump P-7 trip", obs0, tricky)
          human, ActivateInvestigation
          human, RecordObservation(obs1, "Second trip at 05:40", Some(baseTime.AddHours 2.0))
          human, RecordHypothesis(h1, "Bearing wear raised motor load", Some "Friction increases current draw")
          human, RecordHypothesis(h2, "Supply voltage sag", None)
          human, RecordHypothesis(h3, "Clogged intake strainer", None)
          human, AdmitEvidence(e1, TextInput "Vibration 12 mm/s at drive end", Measurement, "Vibration logger VL-2", Some baseTime)
          human, AdmitEvidence(e2, UrlInput "https://example.org/logs/voltage.csv", Log, "SCADA export", None)
          human, AdmitEvidence(e3, TextInput "Strainer 60% blocked", DirectObservation, "Field technician", None)
          human, AdmitEvidence(e4, UrlInput "https://example.org/logs/voltage-v2.csv", Log, "SCADA export (corrected)", None)
          human, SupersedeEvidence(e2, e4, "The first export had a timezone error")
          human, VerifyEvidence(e1, Verified)
          human, RelateEvidence(r1, e1, h1, Supports, Some "Vibration signature indicates bearing damage")
          human, RelateEvidence(r2, e3, h3, Supports, None)
          human, RelateEvidence(r3, e4, h2, Contradicts, Some "Voltage stayed within 2%")
          human, TransitionHypothesis(h1, MarkPlausible, "Consistent with the trip pattern", [])
          human, TransitionHypothesis(h1, MarkSupported, "Vibration evidence", [ e1 ])
          human, TransitionHypothesis(h3, MarkPlausible, "Strainer inspected", [])
          human, TransitionHypothesis(h3, MarkSupported, "Strainer blockage observed", [ e3 ])
          human, TransitionHypothesis(h2, Falsify, "Voltage log shows no sag", [ e4 ])
          human, IdentifyUnknown(u1, "Was the lubrication schedule followed?", Material)
          human, TransitionUnknown(u1, ResolveUnknown, "Maintenance log shows a skipped lubrication")
          human, RecordAssumption(a1, "The vibration sensor is calibrated", Material)
          human, TransitionAssumption(a1, VerifyAssumption)
          human, RecordContradiction(c1, "Vibration high but bearing temperature normal", [ e1 ], Material)
          human, TransitionContradiction(c1, ResolveContradiction, "Temperature sensor lags by 20 minutes")
          human, RecordPrediction(p1, h1, "The outer race shows pitting", DiscriminatingPrediction, Prospective)
          human, RecordAction(x1, Inspection, "Inspect the drive-end bearing", [ h1 ])
          human, TransitionAction(x1, ApproveAction)
          human, RecordFinding(f1, x1, "Outer race pitting observed", Some "Consistent with bearing wear")
          human, EstablishNegativeKnowledge(n1, "Supply voltage did not sag", "Trip window 03:00–03:30", [ e4 ]) ]

    let private closingSteps: (Actor * Command) list =
        [ human,
          RecordCausalAssertion(
              k1,
              "Pump P-7 trip",
              [ { Hypothesis = h1; Role = RootCause }; { Hypothesis = h3; Role = ContributingFactor } ],
              Some "Worn bearing plus restricted intake raised current above the trip setting"
          )
          human, ConfirmCause k1
          human, RecordCorrectiveAction(m1, Mitigation, "Reduce duty cycle to 60%", [ k1 ])
          human, RecordCorrectiveAction(cr1, Correction, "Replace the drive-end bearing", [ k1 ])
          human, RecordCriterion(q1, cr1, "Vibration below 4 mm/s for 24 h", Some "Vibration logger trend")
          human, TransitionCorrection(cr1, ApproveCorrection)
          human, TransitionCorrection(cr1, StartCorrection)
          human, TransitionCorrection(cr1, MarkImplemented)
          human, TransitionCorrection(cr1, StartVerificationOfEffect)
          human, MarkAwaitingVerification
          human, AdmitEvidence(e5, TextInput "Vibration 2.1 mm/s over 24 h", Measurement, "Vibration logger VL-2", None)
          human, RecordVerification(v1, q1, Satisfied, [ e5 ])
          human, RecordConclusion(con1, "Bearing wear caused the trip", ConfirmedCause, [ k1 ], Some "Why lubrication was skipped is not established")
          agent, RecordProposal(pr1, ProposedHypothesis, "Motor winding insulation degraded", Some "Common in older pumps")
          human, AcceptProposal(pr1, hAccepted)
          // C1: an alternative raised after confirmation must be addressed before resolution.
          human, TransitionHypothesis(Id.create hAccepted, Supersede, "Insulation resistance test passed; not involved", [])
          agent, RecordProposal(pr2, ProposedUnknown, "Was ambient temperature high?", None)
          human, RejectProposal(pr2, "Not relevant to the trip")
          human, ResolveInvestigation
          human, CloseInvestigation ]

    let private reopenSteps: (Actor * Command) list =
        [ human, ReopenInvestigation "A new vibration spike after replacement"
          human, ReopenCause(k1, "Spike after the bearing replacement") ]

    let run (steps: (Actor * Command) list) : Event list =
        steps
        |> List.indexed
        |> List.fold
            (fun (state: Investigation option, events: Event list) (i, (actor, command)) ->
                let env =
                    { CommandId = g (1000 + i)
                      Investigation = iid
                      ExpectedVersion = None
                      Actor = actor
                      RequestedAt = baseTime.AddMinutes(float i)
                      Command = command }
                match Decide.execute state env with
                | Ok produced -> (produced |> List.fold (fun s e -> Some(Evolve.apply s e)) state), events @ produced
                | Error errors -> failwithf "step %d refused: %A" i errors)
            (None, [])
        |> snd

    let interimHistory = run interimSteps
    let closedHistory = run (interimSteps @ closingSteps)
    let fullHistory = run (interimSteps @ closingSteps @ reopenSteps)

    let replay events =
        match Evolve.replay events with
        | Some inv -> inv
        | None -> failwith "empty history"

let allEventTypes =
    [ "InvestigationStarted"; "LifecycleChanged"; "ObservationRecorded"; "HypothesisRecorded"; "HypothesisTransitioned"
      "EvidenceAdmitted"; "EvidenceSuperseded"; "EvidenceVerificationAssessed"; "EvidenceRelated"; "UnknownIdentified"
      "UnknownTransitioned"; "AssumptionRecorded"; "AssumptionTransitioned"; "ContradictionRecorded"; "ContradictionTransitioned"
      "PredictionRecorded"; "ActionRecorded"; "ActionTransitioned"; "FindingRecorded"; "NegativeKnowledgeEstablished"
      "CausalAssertionRecorded"; "CausalAssertionConfirmed"; "CausalAssertionReopened"; "CorrectiveActionRecorded"
      "CorrectiveActionTransitioned"; "CriterionRecorded"; "VerificationRecorded"; "ConclusionRecorded"; "ProposalRecorded"
      "ProposalWasAccepted"; "ProposalWasRejected" ]

let private decodeOk json =
    match EventCodec.decode json with
    | Ok v -> v
    | Error e -> failwithf "decode failed: %s" e

let child (key: string) (node: JsonNode) : JsonNode = nonNull node[key]
let textAt (path: string list) (node: JsonNode) = (path |> List.fold (fun n k -> child k n) node).GetValue<string>()
let items (key: string) (node: JsonNode) = (child key node).AsArray() |> Seq.map nonNull |> List.ofSeq
let parseObject (json: string) = (nonNull (JsonNode.Parse json)).AsObject()

let private mutate (f: JsonObject -> unit) (json: string) =
    let root = parseObject json
    f root
    root.ToJsonString()

let private eventAt (i: int) (root: JsonObject) =
    let all = items "events" root
    all[i].AsObject()

[<Fact>]
let ``R10.2 the scenario history exercises every EventBody case`` () =
    let types = Scenario.fullHistory |> List.map (fun e -> EventCodec.typeName e.Body) |> Set.ofList
    Assert.Equal<Set<string>>(Set.ofList allEventTypes, types)
    Assert.Equal(31, allEventTypes.Length)

[<Fact>]
let ``R10.1 R14.1 encode then decode round-trips every event exactly`` () =
    let json = EventCodec.encode Scenario.iid Scenario.fullHistory
    let iid, events = decodeOk json
    Assert.Equal(Scenario.iid, iid)
    Assert.Equal<Event list>(Scenario.fullHistory, events)
    Assert.Equal(Scenario.replay Scenario.fullHistory, Scenario.replay events)

[<Fact>]
let ``R10.2 encoding is canonical: encode (decode doc) is the same document`` () =
    let json = EventCodec.encode Scenario.iid Scenario.fullHistory
    let iid, events = decodeOk json
    Assert.Equal(json, EventCodec.encode iid events)

[<Fact>]
let ``R10.2 the document has the declared envelope and event shape`` () =
    let json = EventCodec.encode Scenario.iid Scenario.fullHistory
    let root = parseObject json
    Assert.Equal("rci.events/v1", textAt [ "schema" ] root)
    Assert.Equal(Id.text Scenario.iid, textAt [ "investigation" ] root)
    let events = items "events" root
    Assert.Equal(Scenario.fullHistory.Length, events.Length)
    let first = events[0].AsObject()
    for key in [ "seq"; "commandId"; "actor"; "at"; "type"; "data" ] do
        Assert.True(first.ContainsKey key, key)
    Assert.Equal(1L, (child "seq" first).GetValue<int64>())
    Assert.Equal("InvestigationStarted", textAt [ "type" ] first)
    Assert.Equal("human", textAt [ "actor"; "kind" ] first)
    Assert.Equal(Scenario.baseTime.ToString("O"), textAt [ "at" ] first)
    let agentEvent = events |> Seq.find (fun e -> textAt [ "actor"; "kind" ] e = "agent")
    Assert.Equal("anthropic", textAt [ "actor"; "provider" ] agentEvent)
    Assert.Equal("claude-opus-5-5", textAt [ "actor"; "model" ] agentEvent)
    Assert.Contains("\n  \"events\": [", json)

[<Fact>]
let ``D-002 text with unicode, newlines and quotes is preserved byte-for-byte`` () =
    let json = EventCodec.encode Scenario.iid Scenario.fullHistory
    let _, events = decodeOk json
    match events.Head.Body with
    | InvestigationStarted(_, _, original) -> Assert.Equal(Scenario.tricky, original.Value)
    | _ -> failwith "first event must start the investigation"
    Assert.Contains("température élevée ✓", json)

[<Fact>]
let ``D-011 an unknown event type is an error, never silently dropped`` () =
    let json =
        EventCodec.encode Scenario.iid Scenario.interimHistory
        |> mutate (fun root -> (eventAt 2 root)["type"] <- JsonValue.Create("HypothesisTeleported"))
    match EventCodec.decode json with
    | Error e -> Assert.Contains("HypothesisTeleported", e)
    | Ok _ -> failwith "an unknown type must not decode"

[<Fact>]
let ``D-012 unknown fields are tolerated at every level`` () =
    let json =
        EventCodec.encode Scenario.iid Scenario.fullHistory
        |> mutate (fun root ->
            root["extension"] <- JsonValue.Create("x")
            for e in items "events" root do
                let o = e.AsObject()
                o["future"] <- JsonObject()
                (child "actor" o).AsObject()["runtime"] <- JsonValue.Create("codex")
                (child "data" o).AsObject()["unexpected"] <- JsonValue.Create(42))
    let _, events = decodeOk json
    Assert.Equal<Event list>(Scenario.fullHistory, events)

[<Fact>]
let ``R10.4 a sequence gap or duplicate is detected`` () =
    let json =
        EventCodec.encode Scenario.iid Scenario.interimHistory
        |> mutate (fun root -> (eventAt 3 root)["seq"] <- JsonValue.Create(99L))
    Assert.True(Result.isError (EventCodec.decode json))

[<Fact>]
let ``R10.2 a wrong schema, malformed JSON or empty text is an error`` () =
    let json =
        EventCodec.encode Scenario.iid Scenario.interimHistory
        |> mutate (fun root -> root["schema"] <- JsonValue.Create("rci.events/v0"))
    Assert.True(Result.isError (EventCodec.decode json))
    Assert.True(Result.isError (EventCodec.decode "{ not json"))
    Assert.True(Result.isError (EventCodec.decode ""))
    Assert.True(Result.isError (EventCodec.decode "[]"))

[<Fact>]
let ``A31 the digest is stable and identifies the snapshot`` () =
    let a = EventCodec.encode Scenario.iid Scenario.closedHistory
    let b = EventCodec.encode Scenario.iid Scenario.closedHistory
    Assert.Equal(EventCodec.digest a, EventCodec.digest b)
    Assert.StartsWith("sha256:", EventCodec.digest a)
    Assert.Equal(7 + 64, (EventCodec.digest a).Length)
    Assert.NotEqual<string>(EventCodec.digest a, EventCodec.digest (EventCodec.encode Scenario.iid Scenario.fullHistory))
    Assert.Equal("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", EventCodec.digest "")

[<Fact>]
let ``R10.2 the empty history round-trips`` () =
    let iid, events = decodeOk (EventCodec.encode Scenario.iid [])
    Assert.Equal(Scenario.iid, iid)
    Assert.Empty(events)

[<Fact>]
let ``R10.1 the investigation index round-trips`` () =
    let entries: IndexCodec.Entry list =
        [ { Id = Scenario.iid; Title = Scenario.tricky; Lifecycle = "Active"; Version = 42L; UpdatedAt = Scenario.baseTime }
          { Id = Id.create (Scenario.g 2); Title = "Second"; Lifecycle = "Closed"; Version = 7L; UpdatedAt = Scenario.baseTime.AddDays 1.0 } ]
    let json = IndexCodec.encode entries
    Assert.Equal<IndexCodec.Entry list>(entries, (IndexCodec.decode json |> Result.defaultWith failwith))
    Assert.Equal<IndexCodec.Entry list>([], (IndexCodec.decode (IndexCodec.encode []) |> Result.defaultWith failwith))
    Assert.True(Result.isError (IndexCodec.decode "{\"schema\":\"other\",\"investigations\":[]}"))
