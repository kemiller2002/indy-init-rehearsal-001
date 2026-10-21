module App.Engine.Tests.DomainTests

// Slice A: domain verification. Table-driven structural tests
// (STATE-TRANSITION-TABLES §17) and requirement/scenario tests whose names
// start with the ids they prove (REQUIREMENTS R1–R8, R15; ACCEPTANCE A1–A40).

open System
open Microsoft.FSharp.Reflection
open Xunit
open IndyInitRootCauseInvestigator.Engine

// ---------------------------------------------------------------------------
// Identifiers and actors (deterministic, so every run is the same run).
// ---------------------------------------------------------------------------

let private gid (n: int) = Guid.Parse(sprintf "00000000-0000-0000-0000-%012d" n)
let private iid: InvestigationId = Id.create (gid 1)
let private obs n : ObservationId = Id.create (gid (100 + n))
let private hyp n : HypothesisId = Id.create (gid (1000 + n))
let private ev n : EvidenceId = Id.create (gid (2000 + n))
let private rel n : RelationshipId = Id.create (gid (3000 + n))
let private unk n : UnknownId = Id.create (gid (4000 + n))
let private asm n : AssumptionId = Id.create (gid (5000 + n))
let private con n : ContradictionId = Id.create (gid (6000 + n))
let private pred n : PredictionId = Id.create (gid (7000 + n))
let private act n : ActionId = Id.create (gid (8000 + n))
let private fnd n : FindingId = Id.create (gid (9000 + n))
let private neg n : NegativeKnowledgeId = Id.create (gid (10000 + n))
let private cause n : CausalAssertionId = Id.create (gid (11000 + n))
let private corr n : CorrectiveActionId = Id.create (gid (12000 + n))
let private crit n : CriterionId = Id.create (gid (13000 + n))
let private ver n : VerificationId = Id.create (gid (14000 + n))
let private concl n : ConclusionId = Id.create (gid (15000 + n))
let private prop n : ProposalId = Id.create (gid (16000 + n))

let private t0 = DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero)
let private alice = Actor.human "alice"
let private bot = Actor.agent "bot-1" "anthropic" "claude"

let private original = "Orders started failing after the 2 PM deployment."

// ---------------------------------------------------------------------------
// A small pure DSL: a history is an event list; commands run against its replay.
// ---------------------------------------------------------------------------

let private envelope actor expected (history: Event list) command =
    { CommandId = gid (900000 + List.length history)
      Investigation = iid
      ExpectedVersion = expected
      Actor = actor
      RequestedAt = t0.AddMinutes(float (List.length history))
      Command = command }

let private stateOf (history: Event list) = Evolve.replay history |> Option.get

let private executeAs actor expected command (history: Event list) =
    Decide.execute (Evolve.replay history) (envelope actor expected history command)

/// Run setup commands in order; a refused setup step fails the test loudly.
let private scriptAs actor commands (history: Event list) =
    commands
    |> List.fold
        (fun h c ->
            match executeAs actor None c h with
            | Ok events -> h @ events
            | Error errors -> failwithf "setup step %A was refused: %A" c errors)
        history

let private script commands history = scriptAs alice commands history
let private given commands = script commands []

/// The command under test: Ok (new events, post-state) or the refusal.
let private attemptAs actor command (history: Event list) =
    executeAs actor None command history
    |> Result.map (fun events -> events, stateOf (history @ events))

let private attempt command history = attemptAs alice command history

let private codesOf (errors: DomainError list) = errors |> List.map (fun e -> e.Code)

let private codes outcome =
    match outcome with
    | Ok _ -> []
    | Error errors -> codesOf errors

let private accepted outcome =
    match outcome with
    | Ok(events, state) -> events, state
    | Error errors -> failwithf "expected acceptance, refused: %A" errors

let private stateAfter outcome = accepted outcome |> snd

let private refused code outcome =
    match outcome with
    | Ok(events: Event list, _) -> failwithf "expected refusal %s, accepted: %A" code (events |> List.map (fun e -> e.Body))
    | Error errors -> Assert.Contains(code, codesOf errors)

let private bodies (events: Event list) = events |> List.map (fun e -> e.Body)

// Fixtures, built only through the single decision path.

let private start = StartInvestigation(iid, "Checkout failures", obs 0, original)
let private opened = given [ start; ActivateInvestigation ]

let private admit n text = AdmitEvidence(ev n, TextInput text, Log, "production log", Some t0)
let private memberOf role n : CausalMember = { Hypothesis = hyp n; Role = role }

let private supportedHypothesis n =
    [ RecordHypothesis(hyp n, $"Hypothesis {n}", Some "mechanism")
      admit n $"Evidence for hypothesis {n}"
      RelateEvidence(rel n, ev n, hyp n, Supports, None)
      TransitionHypothesis(hyp n, MarkPlausible, "fits the timeline", [])
      TransitionHypothesis(hyp n, MarkSupported, "the log shows it", [ ev n ]) ]

let private confirmed =
    opened
    |> script (
        supportedHypothesis 1
        @ [ RecordCausalAssertion(cause 1, "Checkout fails", [ memberOf RootCause 1 ], Some "serializer drops a field")
            ConfirmCause(cause 1) ]
    )

let private implementedCorrection kind =
    [ RecordCorrectiveAction(corr 1, kind, "Revert the serializer change", [ cause 1 ])
      RecordCriterion(crit 1, corr 1, "Checkout error rate below 0.1% for an hour", Some "dashboard")
      TransitionCorrection(corr 1, ApproveCorrection)
      TransitionCorrection(corr 1, StartCorrection)
      TransitionCorrection(corr 1, MarkImplemented) ]

let private verifiedCorrection kind =
    implementedCorrection kind
    @ [ TransitionCorrection(corr 1, StartVerificationOfEffect)
        admit 60 "Checkout error rate 0.01% over the last hour"
        RecordVerification(ver 1, crit 1, Satisfied, [ ev 60 ]) ]

let private conclusion = RecordConclusion(concl 1, "The serializer change caused the failures.", ConfirmedCause, [ cause 1 ], None)

/// Confirmed cause + verified correction + conclusion: the resolution gate holds.
let private resolvable = confirmed |> script (verifiedCorrection Correction @ [ conclusion ])

// ---------------------------------------------------------------------------
// 1. Structural tables: every state x command (STATE-TRANSITION-TABLES §17).
// ---------------------------------------------------------------------------

let private allCases<'T> () : 'T list =
    FSharpType.GetUnionCases(typeof<'T>)
    |> Array.map (fun c -> FSharpValue.MakeUnion(c, [||]) :?> 'T)
    |> List.ofArray

/// Compares the kernel table and the decision path with the spec's legal
/// pairs; every other pair must be None and refused with TRANSITION.ILLEGAL.
let private checkTable name states commands legal table (viaDecide: 'S -> 'C -> Result<'S, string list>) =
    let expected s c = legal |> List.tryPick (fun (f, k, t) -> if f = s && k = c then Some t else None)
    let pairs = List.allPairs states commands
    let tableMismatches =
        pairs
        |> List.choose (fun (s, c) ->
            let got = table s c
            if got = expected s c then None
            else Some(sprintf "%s table: %A x %A expected %A, got %A" name s c (expected s c) got))
    let decideMismatches =
        pairs
        |> List.choose (fun (s, c) ->
            match expected s c, viaDecide s c with
            | Some target, Ok actual when actual = target -> None
            | None, Error errors when List.contains "TRANSITION.ILLEGAL" errors -> None
            | exp, got -> Some(sprintf "%s decide: %A x %A expected %A, got %A" name s c exp got))
    Assert.Equal(List.length states * List.length commands, List.length pairs)
    Assert.Empty(tableMismatches @ decideMismatches)

/// One entity of every kind, so any entity state can be set directly.
let private tableBase =
    opened
    |> script
        [ RecordHypothesis(hyp 1, "H1", None)
          admit 1 "E1"
          IdentifyUnknown(unk 1, "U1?", Material)
          RecordAssumption(asm 1, "A1", Material)
          RecordContradiction(con 1, "C1", [ ev 1 ], Material)
          RecordAction(act 1, Test, "Run the check", [ hyp 1 ])
          RecordCorrectiveAction(corr 1, Correction, "Fix", [])
          RecordCriterion(crit 1, corr 1, "Works", None) ]
    |> stateOf

let private decideOn command (inv: Investigation) =
    Decide.execute (Some inv) (envelope alice None [] command)
    |> Result.map (List.fold (fun s e -> Evolve.apply (Some s) e) inv)
    |> Result.mapError codesOf

let private setState key f (m: Map<'K, 'V>) = m |> Map.change key (Option.map f)

[<Fact>]
let ``[§2][R2.2] hypothesis table: every state x command`` () =
    checkTable
        "hypothesis"
        (allCases<HypothesisState> ())
        (allCases<HypothesisCommand> ())
        [ Proposed, MarkPlausible, Plausible
          Plausible, MarkSupported, Supported
          Plausible, MarkWeakened, Weakened
          Supported, MarkWeakened, Weakened
          Proposed, Falsify, Falsified
          Plausible, Falsify, Falsified
          Supported, Falsify, Falsified
          Weakened, Falsify, Falsified
          Proposed, Supersede, Superseded
          Plausible, Supersede, Superseded
          Supported, Supersede, Superseded
          Weakened, Supersede, Superseded
          Falsified, Reopen, Plausible
          Weakened, MarkPlausible, Plausible
          Weakened, MarkSupported, Supported ]
        Transitions.hypothesis
        (fun s c ->
            { tableBase with Hypotheses = tableBase.Hypotheses |> setState (hyp 1) (fun h -> { h with State = s }) }
            |> decideOn (TransitionHypothesis(hyp 1, c, "reason", [ ev 1 ]))
            |> Result.map (fun inv -> inv.Hypotheses[hyp 1].State))

[<Fact>]
let ``[§6][R5.1] unknown table: every state x command`` () =
    checkTable
        "unknown"
        (allCases<UnknownState> ())
        (allCases<UnknownCommand> ())
        [ Identified, StartInvestigatingUnknown, Investigating
          Identified, ResolveUnknown, UnknownResolved
          Investigating, ResolveUnknown, UnknownResolved
          Identified, AcceptUnknownAsResidual, AcceptedResidual
          Investigating, AcceptUnknownAsResidual, AcceptedResidual ]
        Transitions.unknown
        (fun s c ->
            { tableBase with Unknowns = tableBase.Unknowns |> setState (unk 1) (fun u -> { u with State = s }) }
            |> decideOn (TransitionUnknown(unk 1, c, "basis"))
            |> Result.map (fun inv -> inv.Unknowns[unk 1].State))

[<Fact>]
let ``[§8][R3.4] contradiction table: every state x command`` () =
    checkTable
        "contradiction"
        (allCases<ContradictionState> ())
        (allCases<ContradictionCommand> ())
        [ Detected, TriageContradiction, Triaged
          Detected, ResolveContradiction, ContradictionResolved
          Triaged, ResolveContradiction, ContradictionResolved
          ContradictionInvestigating, ResolveContradiction, ContradictionResolved
          Detected, AcceptContradictionAsResidual, ContradictionAcceptedResidual
          Triaged, AcceptContradictionAsResidual, ContradictionAcceptedResidual
          ContradictionInvestigating, AcceptContradictionAsResidual, ContradictionAcceptedResidual ]
        Transitions.contradiction
        (fun s c ->
            { tableBase with Contradictions = tableBase.Contradictions |> setState (con 1) (fun x -> { x with State = s }) }
            |> decideOn (TransitionContradiction(con 1, c, "basis"))
            |> Result.map (fun inv -> inv.Contradictions[con 1].State))

[<Fact>]
let ``[§3] assumption table: every state x command`` () =
    checkTable
        "assumption"
        (allCases<AssumptionState> ())
        (allCases<AssumptionCommand> ())
        [ ExplicitUnverified, ProvisionallyAcceptAssumption, ProvisionallyAccepted
          ExplicitUnverified, VerifyAssumption, AssumptionVerified
          ProvisionallyAccepted, VerifyAssumption, AssumptionVerified
          ExplicitUnverified, RejectAssumption, Rejected
          ProvisionallyAccepted, RejectAssumption, Rejected ]
        Transitions.assumption
        (fun s c ->
            { tableBase with Assumptions = tableBase.Assumptions |> setState (asm 1) (fun a -> { a with State = s }) }
            |> decideOn (TransitionAssumption(asm 1, c))
            |> Result.map (fun inv -> inv.Assumptions[asm 1].State))

[<Fact>]
let ``[§5][R4.1] action table: every state x command`` () =
    checkTable
        "action"
        (allCases<ActionState> ())
        (allCases<ActionCommand> ())
        [ ActionProposed, ApproveAction, ActionApproved
          ActionProposed, RejectAction, ActionRejected
          ActionApproved, StartAction, ActionInProgress
          ActionApproved, CancelAction, ActionCancelled
          ActionInProgress, CancelAction, ActionCancelled ]
        Transitions.action
        (fun s c ->
            { tableBase with Actions = tableBase.Actions |> setState (act 1) (fun a -> { a with State = s }) }
            |> decideOn (TransitionAction(act 1, c))
            |> Result.map (fun inv -> inv.Actions[act 1].State))

[<Fact>]
let ``[§11][R7.1] corrective action table: every state x command`` () =
    checkTable
        "correction"
        (allCases<CorrectiveActionState> ())
        (allCases<CorrectionCommand> ())
        [ CorrectionProposed, ApproveCorrection, CorrectionApproved
          CorrectionProposed, CancelCorrection, CorrectionCancelled
          CorrectionApproved, CancelCorrection, CorrectionCancelled
          CorrectionApproved, StartCorrection, CorrectionInProgress
          CorrectionInProgress, MarkImplemented, Implemented
          Implemented, StartVerificationOfEffect, AwaitingVerificationOfEffect
          VerificationFailed, RequireRework, ReworkRequired
          ReworkRequired, StartCorrection, CorrectionInProgress ]
        Transitions.correction
        (fun s c ->
            { tableBase with CorrectiveActions = tableBase.CorrectiveActions |> setState (corr 1) (fun x -> { x with State = s }) }
            |> decideOn (TransitionCorrection(corr 1, c))
            |> Result.map (fun inv -> inv.CorrectiveActions[corr 1].State))

[<Fact>]
let ``[§1][R8.1] investigation lifecycle table: every state x command`` () =
    // Resolution gate satisfied and an implemented mitigation awaiting
    // verification, so only structure decides each pair.
    let ready =
        resolvable
        |> script
            [ RecordCorrectiveAction(corr 2, Mitigation, "Rate-limit checkout", [ cause 1 ])
              TransitionCorrection(corr 2, ApproveCorrection)
              TransitionCorrection(corr 2, StartCorrection)
              TransitionCorrection(corr 2, MarkImplemented) ]
        |> stateOf
    let activate = ActivateInvestigation
    let awaitEvidence = MarkAwaitingEvidence "waiting on vendor logs"
    let reopen = ReopenInvestigation "customers report new failures"
    checkTable
        "lifecycle"
        (allCases<InvestigationLifecycle> ())
        [ activate; awaitEvidence; MarkAwaitingVerification; ResolveInvestigation; CloseInvestigation; reopen ]
        [ Draft, activate, Active
          Active, awaitEvidence, AwaitingEvidence
          AwaitingEvidence, activate, Active
          Active, MarkAwaitingVerification, AwaitingVerification
          AwaitingVerification, activate, Active
          AwaitingVerification, ResolveInvestigation, Resolved
          Active, ResolveInvestigation, Resolved
          Resolved, CloseInvestigation, Closed
          Resolved, reopen, Active
          Closed, reopen, Active
          Archived, activate, Active ]
        Transitions.lifecycle
        (fun s c -> { ready with Lifecycle = s } |> decideOn c |> Result.map (fun inv -> inv.Lifecycle))

[<Fact>]
let ``[§2][R2.3] hypothesis evidence requirement table`` () =
    let expected = [ MarkSupported; MarkWeakened; Falsify; Reopen ] |> Set.ofList
    allCases<HypothesisCommand> ()
    |> List.iter (fun c -> Assert.Equal(expected.Contains c, Transitions.requiresEvidence c))

[<Fact>]
let ``[D-005] content is open only while Draft, Active or awaiting`` () =
    allCases<InvestigationLifecycle> ()
    |> List.iter (fun s ->
        let expected = [ Draft; Active; AwaitingEvidence; AwaitingVerification ] |> List.contains s
        Assert.Equal(expected, Transitions.isOpen s))

// ---------------------------------------------------------------------------
// R1 Investigation creation
// ---------------------------------------------------------------------------

[<Fact>]
let ``[R1.1][R1.3] starting an investigation yields a Draft with a stable id and creation timestamp`` () =
    let events, inv = [] |> attempt start |> accepted
    Assert.Equal<EventBody list>([ InvestigationStarted(NonEmptyText.tryCreate "Checkout failures" |> Option.get, obs 0, NonEmptyText.tryCreate original |> Option.get) ], bodies events)
    Assert.Equal(iid, inv.Id)
    Assert.Equal(Draft, inv.Lifecycle)
    Assert.Equal(t0, inv.CreatedAt)
    Assert.Equal(1L, inv.Version)
    let later = stateOf resolvable
    Assert.Equal(iid, later.Id)
    Assert.Equal(t0, later.CreatedAt)
    Assert.All(resolvable, fun e -> Assert.Equal(iid, e.Investigation))

[<Fact>]
let ``[R1.2][A1] original observation text is preserved verbatim`` () =
    let raw = "  Orders started failing after the 2 PM deployment —\n  «café» totals off by 0.01  "
    let inv = [] |> attempt (StartInvestigation(iid, "", obs 0, raw)) |> stateAfter
    Assert.Equal(raw, inv.Observations[obs 0].Original.Value)
    Assert.Equal(raw, inv.Title.Value)
    // A1: the causal reading is not established evidence.
    Assert.Empty(inv.Evidence)
    Assert.Empty(inv.Hypotheses)

[<Fact>]
let ``[R1.2] later observations keep their original text and provenance`` () =
    let text = "Retries succeed after ~30s\t(eu-west only)"
    let inv = opened |> attemptAs bot (RecordObservation(obs 1, text, Some t0)) |> stateAfter
    let o = inv.Observations[obs 1]
    Assert.Equal(text, o.Original.Value)
    Assert.Equal(Some t0, o.ObservedAt)
    Assert.Equal(bot, o.Provenance.Actor)

[<Fact>]
let ``[R1.1] start is refused for an empty observation, an existing id, or a mismatched envelope`` () =
    [] |> attempt (StartInvestigation(iid, "t", obs 0, "   ")) |> refused "VALIDATION.EMPTY"
    opened |> attempt start |> refused "INVESTIGATION.EXISTS"
    [] |> attempt (StartInvestigation(Id.create (gid 2), "t", obs 0, original)) |> refused "ENVELOPE.MISMATCH"
    [] |> attempt ActivateInvestigation |> refused "INVESTIGATION.NOT_FOUND"

// ---------------------------------------------------------------------------
// R2 Hypotheses
// ---------------------------------------------------------------------------

[<Fact>]
let ``[R2.1][R2.2][A2] multiple hypotheses coexist with explicit states`` () =
    let inv =
        opened
        |> script (
            supportedHypothesis 1
            @ [ RecordHypothesis(hyp 2, "Upstream outage", None)
                RecordHypothesis(hyp 3, "Exhausted connection pool", None) ]
        )
        |> stateOf
    Assert.Equal(3, inv.Hypotheses.Count)
    Assert.Equal(Supported, inv.Hypotheses[hyp 1].State)
    Assert.Equal(Proposed, inv.Hypotheses[hyp 2].State)
    Assert.Equal(Proposed, inv.Hypotheses[hyp 3].State)

[<Fact>]
let ``[R2.3] a hypothesis transition records from, to, reason and evidence`` () =
    let history = opened |> script [ RecordHypothesis(hyp 1, "Deployment regression", None); admit 1 "500s from the new build"; TransitionHypothesis(hyp 1, MarkPlausible, "fits the timeline", []) ]
    let events, inv = history |> attempt (TransitionHypothesis(hyp 1, MarkSupported, "the new build throws", [ ev 1 ])) |> accepted
    let change = inv.Hypotheses[hyp 1].History |> List.last
    Assert.Equal(Plausible, change.From)
    Assert.Equal(Supported, change.To)
    Assert.Equal("MarkSupported", change.Command)
    Assert.Equal("the new build throws", change.Reason.Value)
    Assert.Equal<EvidenceId list>([ ev 1 ], change.Evidence)
    Assert.Equal((List.exactlyOne events).Sequence, change.Sequence)
    Assert.Equal(2, inv.Hypotheses[hyp 1].History.Length)

[<Fact>]
let ``[R2.3] Supported, Weakened, Falsified and Reopen require cited evidence; every transition requires a reason`` () =
    let history =
        opened
        |> script
            [ RecordHypothesis(hyp 1, "H1", None)
              TransitionHypothesis(hyp 1, MarkPlausible, "relevant", [])
              RecordHypothesis(hyp 2, "H2", None)
              admit 1 "E1"
              TransitionHypothesis(hyp 2, Falsify, "ruled out", [ ev 1 ]) ]
    [ TransitionHypothesis(hyp 1, MarkSupported, "r", [])
      TransitionHypothesis(hyp 1, MarkWeakened, "r", [])
      TransitionHypothesis(hyp 1, Falsify, "r", [])
      TransitionHypothesis(hyp 2, Reopen, "r", []) ]
    |> List.iter (fun c -> history |> attempt c |> refused "HYPOTHESIS.EVIDENCE_REQUIRED")
    history |> attempt (TransitionHypothesis(hyp 1, MarkWeakened, " ", [ ev 1 ])) |> refused "VALIDATION.EMPTY"
    history |> attempt (TransitionHypothesis(hyp 1, MarkWeakened, "r", [ ev 99 ])) |> refused "NOT_FOUND"

[<Fact>]
let ``[R2.4][A24] a falsified hypothesis stays in the projection with its linked basis`` () =
    let history =
        opened
        |> script
            [ RecordHypothesis(hyp 1, "Upstream outage", None)
              TransitionHypothesis(hyp 1, MarkPlausible, "possible", [])
              admit 1 "Vendor status page green; their own probes succeed"
              TransitionHypothesis(hyp 1, Falsify, "the upstream was healthy throughout", [ ev 1 ]) ]
    let h = (stateOf history).Hypotheses[hyp 1]
    Assert.Equal(Falsified, h.State)
    Assert.Equal<HypothesisState list>([ Plausible; Falsified ], h.History |> List.map (fun c -> c.To))
    Assert.Equal<EvidenceId list>([ ev 1 ], (List.last h.History).Evidence)
    // Also the event log keeps it.
    Assert.Contains(history, fun e -> match e.Body with HypothesisRecorded(id, _, _) -> id = hyp 1 | _ -> false)

[<Fact>]
let ``[R2.5][A5] reopening a falsified hypothesis needs evidence admitted after the falsification`` () =
    let falsified =
        opened
        |> script
            [ RecordHypothesis(hyp 1, "Clock skew", None)
              admit 1 "NTP offsets under 5 ms"
              TransitionHypothesis(hyp 1, Falsify, "clocks agree", [ ev 1 ]) ]
    falsified |> attempt (TransitionHypothesis(hyp 1, Reopen, "look again", [ ev 1 ])) |> refused "HYPOTHESIS.REOPEN_NEEDS_NEW_EVIDENCE"
    falsified |> attempt (TransitionHypothesis(hyp 1, MarkPlausible, "skip reopen", [ ev 1 ])) |> refused "TRANSITION.ILLEGAL"
    let corrected = falsified |> script [ admit 2 "The NTP probe itself was misconfigured" ]
    let h = (corrected |> attempt (TransitionHypothesis(hyp 1, Reopen, "the falsifying probe was faulty", [ ev 2 ])) |> stateAfter).Hypotheses[hyp 1]
    Assert.Equal(Plausible, h.State)
    let reopening = List.last h.History
    Assert.Equal(Falsified, reopening.From)
    Assert.Equal<EvidenceId list>([ ev 2 ], reopening.Evidence)
    Assert.Equal("the falsifying probe was faulty", reopening.Reason.Value)
    Assert.Contains(h.History, fun c -> c.To = Falsified)

[<Fact>]
let ``[R2.5][A5] a second reopening needs evidence newer than the latest falsification`` () =
    let twiceFalsified =
        opened
        |> script
            [ RecordHypothesis(hyp 1, "Clock skew", None)
              admit 1 "NTP offsets under 5 ms"
              TransitionHypothesis(hyp 1, Falsify, "clocks agree", [ ev 1 ])
              admit 2 "The NTP probe was misconfigured"
              TransitionHypothesis(hyp 1, Reopen, "faulty probe", [ ev 2 ])
              TransitionHypothesis(hyp 1, Falsify, "fixed probe still agrees", [ ev 2 ]) ]
    twiceFalsified |> attempt (TransitionHypothesis(hyp 1, Reopen, "again", [ ev 2 ])) |> refused "HYPOTHESIS.REOPEN_NEEDS_NEW_EVIDENCE"
    twiceFalsified |> script [ admit 3 "A second probe disagrees" ] |> attempt (TransitionHypothesis(hyp 1, Reopen, "again", [ ev 3 ])) |> stateAfter |> ignore

// ---------------------------------------------------------------------------
// R3 Evidence
// ---------------------------------------------------------------------------

[<Fact>]
let ``[R3.1][R3.2] evidence is admitted with source, capture time and verification status`` () =
    let captured = t0.AddHours(-2.0)
    let history = opened |> script [ AdmitEvidence(ev 1, TextInput "HTTP 500 at 14:02", Log, "checkout-api pod logs", Some captured) ]
    let e = (stateOf history).Evidence[ev 1]
    Assert.Equal("checkout-api pod logs", e.Source.Value)
    Assert.Equal(Some captured, e.CapturedAt)
    Assert.Equal(Unverified, e.Verification)
    Assert.Equal(Log, e.Category)
    Assert.Equal(TextEvidence(NonEmptyText.tryCreate "HTTP 500 at 14:02" |> Option.get), e.Content)
    let verified = (history |> attempt (VerifyEvidence(ev 1, Verified)) |> stateAfter).Evidence[ev 1]
    Assert.Equal(Verified, verified.Verification)
    opened |> attempt (AdmitEvidence(ev 1, TextInput "x", Log, " ", None)) |> refused "VALIDATION.EMPTY"
    history |> attempt (admit 1 "again") |> refused "DUPLICATE_ID"

[<Fact>]
let ``[R3.3] evidence links to hypotheses as Supports, Contradicts, Discriminates or Insufficient`` () =
    let kinds = [ Supports; Contradicts; Discriminates; Insufficient ]
    let inv =
        opened
        |> script (
            [ RecordHypothesis(hyp 1, "H1", None); RecordHypothesis(hyp 2, "H2", None) ]
            @ (kinds |> List.mapi (fun i _ -> admit (i + 1) $"E{i + 1}"))
            @ (kinds |> List.mapi (fun i k -> RelateEvidence(rel (i + 1), ev (i + 1), hyp 1, k, Some "why")))
            @ [ RelateEvidence(rel 9, ev 3, hyp 2, Discriminates, None) ]
        )
        |> stateOf
    Assert.Equal<RelationshipKind list>(kinds, [ 1..4 ] |> List.map (fun i -> inv.Relationships[rel i].Kind))
    Assert.Equal<HypothesisId list>([ hyp 1; hyp 2 ], inv.Relationships |> Map.toList |> List.filter (fun (_, r) -> r.Evidence = ev 3) |> List.map (fun (_, r) -> r.Hypothesis))

[<Fact>]
let ``[R3.4][A7][A22] contradictory evidence is recorded, stays visible and blocks confirmation`` () =
    let history =
        confirmed
        |> script
            [ ReopenCause(cause 1, "a second log disagrees")
              admit 2 "Retries on the old build fail identically"
              VerifyEvidence(ev 1, Verified)
              VerifyEvidence(ev 2, Verified)
              RelateEvidence(rel 2, ev 2, hyp 1, Contradicts, None)
              RecordContradiction(con 1, "Old build fails the same way", [ ev 1; ev 2 ], Material) ]
    let inv = stateOf history
    Assert.Equal(Detected, inv.Contradictions[con 1].State)
    Assert.Equal<EvidenceId list>([ ev 1; ev 2 ], inv.Contradictions[con 1].Evidence)
    match history |> attempt (ConfirmCause(cause 1)) with
    | Error errors ->
        let blocker = errors |> List.find (fun e -> e.Code = "CAUSE.OPEN_CONTRADICTION")
        Assert.Contains("Old build fails the same way", blocker.Message)
    | Ok _ -> failwith "confirmation should be blocked by the contradiction"
    let resolved = history |> script [ TransitionContradiction(con 1, ResolveContradiction, "the old build had the same bad config") ]
    let c = (stateOf resolved).Contradictions[con 1]
    Assert.Equal(ContradictionResolved, c.State)
    Assert.Equal(Some "the old build had the same bad config", c.Basis |> Option.map NonEmptyText.value)
    resolved |> attempt (ConfirmCause(cause 1)) |> stateAfter |> ignore

[<Fact>]
let ``[R3.5] an http(s) artifact URL is accepted and anything else is refused`` () =
    let url = "https://artifacts.example.com/runs/42/heap.hprof"
    let inv = opened |> attempt (AdmitEvidence(ev 1, UrlInput url, Measurement, "CI", None)) |> stateAfter
    Assert.Equal(ArtifactEvidence url, inv.Evidence[ev 1].Content)
    opened |> attempt (AdmitEvidence(ev 1, UrlInput "http://example.com/a.log", Log, "CI", None)) |> stateAfter |> ignore
    [ "ftp://example.com/a.log"; "file:///etc/passwd"; "javascript:alert(1)"; "example.com/a.log"; "" ]
    |> List.iter (fun bad -> opened |> attempt (AdmitEvidence(ev 1, UrlInput bad, Log, "CI", None)) |> refused "EVIDENCE.URL")

[<Fact>]
let ``[R3.1][A38] superseding evidence preserves the original and re-evaluates the cause gate`` () =
    let history =
        opened
        |> script (
            supportedHypothesis 1
            @ [ RecordCausalAssertion(cause 1, "Checkout fails", [ memberOf RootCause 1 ], Some "m")
                admit 2 "Corrected measurement from a calibrated probe"
                SupersedeEvidence(ev 1, ev 2, "the first probe was faulty") ]
        )
    let inv = stateOf history
    Assert.Equal(Some(ev 2), inv.Evidence[ev 1].SupersededBy)
    Assert.Equal(TextEvidence(NonEmptyText.tryCreate "Evidence for hypothesis 1" |> Option.get), inv.Evidence[ev 1].Content)
    Assert.Contains(history, fun e -> match e.Body with EvidenceAdmitted(id, _, _, _, _) -> id = ev 1 | _ -> false)
    history |> attempt (ConfirmCause(cause 1)) |> refused "CAUSE.NO_SUPPORTING_EVIDENCE"
    history |> attempt (SupersedeEvidence(ev 1, ev 2, "again")) |> refused "EVIDENCE.ALREADY_SUPERSEDED"
    history |> attempt (SupersedeEvidence(ev 2, ev 2, "self")) |> refused "EVIDENCE.SELF"

// ---------------------------------------------------------------------------
// R4 Experiments and checks
// ---------------------------------------------------------------------------

[<Fact>]
let ``[R4.1] each kind of check can be proposed against hypotheses`` () =
    let kinds = [ Test; Inspection; Query; Comparison; Experiment ]
    let inv =
        opened
        |> script (RecordHypothesis(hyp 1, "H1", None) :: (kinds |> List.mapi (fun i k -> RecordAction(act i, k, $"check {i}", [ hyp 1 ]))))
        |> stateOf
    kinds
    |> List.iteri (fun i k ->
        let a = inv.Actions[act i]
        Assert.Equal(k, a.Kind)
        Assert.Equal(ActionProposed, a.State)
        Assert.Equal<HypothesisId list>([ hyp 1 ], a.Targets))
    opened |> attempt (RecordAction(act 9, Test, "check", [ hyp 9 ])) |> refused "NOT_FOUND"

[<Fact>]
let ``[R4.2][R4.4] the prediction precedes the finding, which preserves the result and completes the action`` () =
    let result = "Error rate: 0.00%  (n=1,204)\nno 500s"
    let history =
        opened
        |> script
            [ RecordHypothesis(hyp 1, "Serializer regression", None)
              RecordPrediction(pred 1, hyp 1, "Reverting the serializer stops the 500s", DiscriminatingPrediction, Prospective)
              RecordAction(act 1, Experiment, "Revert in staging", [ hyp 1 ])
              TransitionAction(act 1, ApproveAction)
              TransitionAction(act 1, StartAction) ]
    let events, inv = history |> attempt (RecordFinding(fnd 1, act 1, result, Some "supports H1")) |> accepted
    let sequenceOf pick = (history @ events) |> List.pick (fun e -> if pick e.Body then Some e.Sequence else None)
    Assert.True(
        sequenceOf (function PredictionRecorded _ -> true | _ -> false) < sequenceOf (function FindingRecorded _ -> true | _ -> false)
    )
    Assert.Equal("Reverting the serializer stops the 500s", inv.Predictions[pred 1].Statement.Value)
    Assert.Equal(Prospective, inv.Predictions[pred 1].Timing)
    Assert.Equal(result, inv.Findings[fnd 1].OriginalResult.Value)
    Assert.Equal(Some "supports H1", inv.Findings[fnd 1].Interpretation |> Option.map NonEmptyText.value)
    Assert.Equal(ActionCompleted, inv.Actions[act 1].State)
    Assert.Empty(inv.Evidence) // R4.4/§13: a finding is not evidence by itself.

[<Fact>]
let ``[R4.4] a finding cannot be recorded for an action that is not runnable`` () =
    let history = opened |> script [ RecordAction(act 1, Test, "check", []); RecordAction(act 2, Test, "check", []); TransitionAction(act 2, RejectAction) ]
    history |> attempt (RecordFinding(fnd 1, act 1, "r", None)) |> refused "ACTION.NOT_EXECUTABLE"
    history |> attempt (RecordFinding(fnd 1, act 2, "r", None)) |> refused "ACTION.NOT_EXECUTABLE"

// ---------------------------------------------------------------------------
// R5 Unknowns and negative knowledge
// ---------------------------------------------------------------------------

[<Fact>]
let ``[R5.1][R5.2][A8] an open blocking unknown blocks cause confirmation until resolved or accepted`` () =
    let history =
        opened
        |> script (
            supportedHypothesis 1
            @ [ IdentifyUnknown(unk 1, "Did the pool config change at 2 PM?", Blocking)
                RecordCausalAssertion(cause 1, "Checkout fails", [ memberOf RootCause 1 ], Some "m") ]
        )
    match history |> attempt (ConfirmCause(cause 1)) with
    | Error errors ->
        Assert.Equal<string list>([ "CAUSE.OPEN_UNKNOWN" ], codesOf errors)
        Assert.Contains("Did the pool config change at 2 PM?", (List.exactlyOne errors).Message)
    | Ok _ -> failwith "an open blocking unknown must block confirmation"
    history |> script [ TransitionUnknown(unk 1, ResolveUnknown, "config unchanged per audit log") ] |> attempt (ConfirmCause(cause 1)) |> stateAfter |> ignore
    let inv = history |> script [ TransitionUnknown(unk 1, AcceptUnknownAsResidual, "audit log unavailable") ] |> attempt (ConfirmCause(cause 1)) |> stateAfter
    Assert.Equal(CauseConfirmed, inv.Causes[cause 1].State)
    Assert.Equal(AcceptedResidual, inv.Unknowns[unk 1].State)

[<Fact>]
let ``[R5.2] an open blocking unknown blocks resolution`` () =
    let history = resolvable |> script [ IdentifyUnknown(unk 1, "Is the second region affected?", Blocking) ]
    history |> attempt ResolveInvestigation |> refused "RESOLVE.BLOCKING_UNKNOWN"
    let inv = history |> script [ TransitionUnknown(unk 1, ResolveUnknown, "region unaffected") ] |> attempt ResolveInvestigation |> stateAfter
    Assert.Equal(Resolved, inv.Lifecycle)

[<Fact>]
let ``[R5.3][A17] negative knowledge requires an evidence basis and keeps its scope`` () =
    let history = opened |> script [ admit 1 "p99 latency flat during the incident" ]
    history |> attempt (EstablishNegativeKnowledge(neg 1, "Network latency", "v2 in eu-west", [])) |> refused "NEGATIVE.BASIS_REQUIRED"
    history |> attempt (EstablishNegativeKnowledge(neg 1, "Network latency", " ", [ ev 1 ])) |> refused "VALIDATION.EMPTY"
    let n = (history |> attempt (EstablishNegativeKnowledge(neg 1, "Network latency", "v2 in eu-west", [ ev 1 ])) |> stateAfter).NegativeKnowledge[neg 1]
    Assert.Equal("v2 in eu-west", n.Scope.Value)
    Assert.Equal<EvidenceId list>([ ev 1 ], n.Basis)

[<Fact>]
let ``[R5.4] an agent proposal repeating a falsified or eliminated explanation is refused; a human may still record it`` () =
    let history =
        opened
        |> script
            [ RecordHypothesis(hyp 1, "Upstream outage", None)
              admit 1 "Vendor probes all green"
              TransitionHypothesis(hyp 1, Falsify, "upstream healthy", [ ev 1 ])
              EstablishNegativeKnowledge(neg 1, "Network latency", "v2 in eu-west", [ ev 1 ]) ]
    history |> attemptAs bot (RecordProposal(prop 1, ProposedHypothesis, "  upstream OUTAGE ", None)) |> refused "PROPOSAL.REPEATS_NEGATIVE_KNOWLEDGE"
    history |> attemptAs bot (RecordProposal(prop 1, ProposedHypothesis, "network latency", None)) |> refused "PROPOSAL.REPEATS_NEGATIVE_KNOWLEDGE"
    history |> attemptAs bot (RecordProposal(prop 1, ProposedHypothesis, "Exhausted connection pool", None)) |> stateAfter |> ignore
    history |> attempt (RecordProposal(prop 1, ProposedHypothesis, "Upstream outage", Some "new vendor incident report")) |> stateAfter |> ignore
    let inv = history |> attempt (RecordHypothesis(hyp 2, "Upstream outage", None)) |> stateAfter
    Assert.Equal(Proposed, inv.Hypotheses[hyp 2].State)

// ---------------------------------------------------------------------------
// R6 Root-cause conclusion
// ---------------------------------------------------------------------------

[<Fact>]
let ``[R6.1][§15] the cause gate returns every blocker at once`` () =
    let history =
        opened
        |> script
            [ RecordHypothesis(hyp 1, "Connection pool exhausted", None)
              RecordHypothesis(hyp 2, "Upstream outage", None)
              admit 1 "Pool metrics show 3/50 connections in use"
              admit 2 "Logs show pool timeouts"
              RecordContradiction(con 1, "Pool metrics disagree with logs", [ ev 1; ev 2 ], Material)
              IdentifyUnknown(unk 1, "Did the pool config change?", Blocking)
              RecordAssumption(asm 1, "Host clocks are synchronised", Material)
              RecordCausalAssertion(cause 1, "Checkout fails", [ memberOf ContributingFactor 1 ], None) ]
    let blockers = history |> attempt (ConfirmCause(cause 1)) |> codes |> Set.ofList
    let expected =
        [ "CAUSE.NO_ROOT"; "CAUSE.MEMBER_NOT_SUPPORTED"; "CAUSE.NO_SUPPORTING_EVIDENCE"; "CAUSE.OPEN_CONTRADICTION"
          "CAUSE.OPEN_UNKNOWN"; "CAUSE.UNVERIFIED_ASSUMPTION"; "CAUSE.ALTERNATIVE_NOT_ADDRESSED"; "CAUSE.NO_MECHANISM" ]
        |> Set.ofList
    Assert.Equal<Set<string>>(expected, blockers)
    // Satisfy every condition, then confirmation succeeds.
    let satisfied =
        history
        |> script
            [ RelateEvidence(rel 1, ev 2, hyp 1, Supports, None)
              TransitionHypothesis(hyp 1, MarkPlausible, "relevant", [])
              TransitionHypothesis(hyp 1, MarkSupported, "timeouts", [ ev 2 ])
              TransitionContradiction(con 1, ResolveContradiction, "metrics sampled the wrong pool")
              TransitionUnknown(unk 1, ResolveUnknown, "config changed at 13:58")
              TransitionAssumption(asm 1, VerifyAssumption)
              TransitionHypothesis(hyp 2, Falsify, "vendor healthy", [ ev 1 ])
              RecordCausalAssertion(cause 2, "Checkout fails", [ memberOf RootCause 1 ], Some "pool size cut to 3") ]
    let events, inv = satisfied |> attempt (ConfirmCause(cause 2)) |> accepted
    Assert.Equal<EventBody list>([ CausalAssertionConfirmed(cause 2, Policy.id) ], bodies events)
    Assert.Equal(CauseConfirmed, inv.Causes[cause 2].State)
    Assert.Equal(Supported, inv.Hypotheses[hyp 1].State) // no "HypothesisConfirmed"

[<Fact>]
let ``[R6.1] a cause cannot be confirmed while the investigation is still Draft`` () =
    let history = given (start :: supportedHypothesis 1 @ [ RecordCausalAssertion(cause 1, "o", [ memberOf RootCause 1 ], Some "m") ])
    history |> attempt (ConfirmCause(cause 1)) |> refused "INVESTIGATION.NOT_OPEN"
    opened |> attempt (ConfirmCause(cause 9)) |> refused "CAUSE.NOT_FOUND"

[<Fact>]
let ``[R6.3][A15][A16] a contributing factor alone is refused; root and contributing roles stay distinct`` () =
    let history = opened |> script (supportedHypothesis 1 @ supportedHypothesis 2)
    let factorOnly = history |> script [ RecordCausalAssertion(cause 1, "Checkout fails", [ memberOf ContributingFactor 2 ], Some "m") ]
    factorOnly |> attempt (ConfirmCause(cause 1)) |> refused "CAUSE.NO_ROOT"
    let inv =
        history
        |> script [ RecordCausalAssertion(cause 2, "Checkout fails", [ memberOf RootCause 1; memberOf ContributingFactor 2 ], Some "m") ]
        |> attempt (ConfirmCause(cause 2))
        |> stateAfter
    Assert.Equal<CauseRole list>([ RootCause; ContributingFactor ], inv.Causes[cause 2].Members |> List.map (fun m -> m.Role))
    // A16: a causal set of two jointly necessary root causes.
    let set =
        history
        |> script [ RecordCausalAssertion(cause 3, "Checkout fails", [ memberOf RootCause 1; memberOf RootCause 2 ], Some "both needed") ]
        |> attempt (ConfirmCause(cause 3))
        |> stateAfter
    Assert.Equal(CauseConfirmed, set.Causes[cause 3].State)
    opened |> attempt (RecordCausalAssertion(cause 4, "o", [], None)) |> refused "CAUSE.NO_MEMBERS"

[<Fact>]
let ``[R6.1][A40] a conclusion cannot claim ConfirmedCause before the cause is confirmed`` () =
    let proposed = opened |> script (supportedHypothesis 1 @ [ RecordCausalAssertion(cause 1, "o", [ memberOf RootCause 1 ], Some "m") ])
    proposed |> attempt (RecordConclusion(concl 1, "Serializer", ConfirmedCause, [ cause 1 ], None)) |> refused "CONCLUSION.UNSUPPORTED"
    proposed |> attempt (RecordConclusion(concl 1, "No cause yet", ConfirmedCause, [], None)) |> refused "CONCLUSION.UNSUPPORTED"
    let inv = proposed |> attempt (RecordConclusion(concl 1, "Serializer, probably", SupportedCause, [ cause 1 ], None)) |> stateAfter
    Assert.Equal(SupportedCause, inv.Conclusions[concl 1].Strength)
    let reopened = confirmed |> script [ ReopenCause(cause 1, "new contradicting log") ]
    reopened |> attempt conclusion |> refused "CONCLUSION.UNSUPPORTED"
    confirmed |> attempt conclusion |> stateAfter |> ignore

[<Fact>]
let ``[R6.4][A23] residual uncertainty is required while unknowns remain open or were accepted`` () =
    let record residual = RecordConclusion(concl 1, "Likely the pool", PlausibleExplanation, [], residual)
    let blocking = opened |> script [ IdentifyUnknown(unk 1, "Pool config?", Blocking) ]
    let material = opened |> script [ IdentifyUnknown(unk 1, "Pool config?", Material) ]
    let accepted' = material |> script [ TransitionUnknown(unk 1, AcceptUnknownAsResidual, "audit log lost") ]
    [ blocking; material; accepted' ]
    |> List.iter (fun h ->
        h |> attempt (record None) |> refused "CONCLUSION.RESIDUAL_REQUIRED"
        h |> attempt (record (Some " ")) |> refused "CONCLUSION.RESIDUAL_REQUIRED")
    let c = (accepted' |> attempt (record (Some "Pool config history is unknown")) |> stateAfter).Conclusions[concl 1]
    Assert.Equal(Some "Pool config history is unknown", c.ResidualUncertainty |> Option.map NonEmptyText.value)
    opened |> script [ IdentifyUnknown(unk 1, "Trivia?", Informational) ] |> attempt (record None) |> stateAfter |> ignore

[<Fact>]
let ``[R6.1][§10] a confirmed cause can be reopened with a reason and confirmed again`` () =
    confirmed |> attempt (ConfirmCause(cause 1)) |> refused "CAUSE.STATE"
    confirmed |> attempt (ReopenCause(cause 1, " ")) |> refused "VALIDATION.EMPTY"
    let reopened = confirmed |> script [ ReopenCause(cause 1, "new contradicting log") ]
    Assert.Equal(CauseReopened, (stateOf reopened).Causes[cause 1].State)
    reopened |> attempt (ReopenCause(cause 1, "again")) |> refused "TRANSITION.ILLEGAL"
    Assert.Equal(CauseConfirmed, (reopened |> attempt (ConfirmCause(cause 1)) |> stateAfter).Causes[cause 1].State)
    Assert.Contains(reopened, fun e -> match e.Body with CausalAssertionConfirmed(id, _) -> id = cause 1 | _ -> false)

// ---------------------------------------------------------------------------
// R7 Corrective action and verification
// ---------------------------------------------------------------------------

[<Fact>]
let ``[R7.1][R7.2][A32] a verified mitigation does not resolve the root cause`` () =
    let history = confirmed |> script (verifiedCorrection Mitigation @ [ conclusion ])
    let inv = stateOf history
    Assert.Equal(Mitigation, inv.CorrectiveActions[corr 1].Kind)
    Assert.Equal(VerifiedEffective, inv.CorrectiveActions[corr 1].State)
    Assert.Equal(CauseConfirmed, inv.Causes[cause 1].State)
    history |> attempt ResolveInvestigation |> refused "RESOLVE.NO_CORRECTION"

[<Fact>]
let ``[R7.3] verification cannot start before success criteria exist`` () =
    let history =
        confirmed
        |> script
            [ RecordCorrectiveAction(corr 1, Correction, "Revert", [ cause 1 ])
              TransitionCorrection(corr 1, ApproveCorrection)
              TransitionCorrection(corr 1, StartCorrection)
              TransitionCorrection(corr 1, MarkImplemented) ]
    history |> attempt (TransitionCorrection(corr 1, StartVerificationOfEffect)) |> refused "CORRECTION.NO_CRITERIA"
    let inv = history |> script [ RecordCriterion(crit 1, corr 1, "No 500s for an hour", None) ] |> attempt (TransitionCorrection(corr 1, StartVerificationOfEffect)) |> stateAfter
    Assert.Equal(AwaitingVerificationOfEffect, inv.CorrectiveActions[corr 1].State)

[<Fact>]
let ``[R7.4][A20] an implemented correction is not verified and does not resolve`` () =
    let history = confirmed |> script (implementedCorrection Correction @ [ admit 60 "deploy log"; conclusion ])
    Assert.Equal(Implemented, (stateOf history).CorrectiveActions[corr 1].State)
    let blockers = history |> attempt ResolveInvestigation |> codes
    Assert.Contains("RESOLVE.CORRECTION_NOT_VERIFIED", blockers)
    Assert.Contains("RESOLVE.CRITERION_UNSATISFIED", blockers)
    history |> attempt (RecordVerification(ver 1, crit 1, Satisfied, [ ev 60 ])) |> refused "VERIFICATION.NOT_AWAITING"

[<Fact>]
let ``[R7.4] a satisfied verification must cite evidence, and every criterion must be satisfied`` () =
    let history =
        confirmed
        |> script (
            implementedCorrection Correction
            @ [ RecordCriterion(crit 2, corr 1, "No duplicate orders", None)
                TransitionCorrection(corr 1, StartVerificationOfEffect)
                admit 60 "error rate 0.01%" ]
        )
    history |> attempt (RecordVerification(ver 1, crit 1, Satisfied, [])) |> refused "VERIFICATION.EVIDENCE_REQUIRED"
    let inconclusive = history |> attempt (RecordVerification(ver 1, crit 1, Inconclusive, [])) |> stateAfter
    Assert.Equal(AwaitingVerificationOfEffect, inconclusive.CorrectiveActions[corr 1].State)
    let one = history |> script [ RecordVerification(ver 1, crit 1, Satisfied, [ ev 60 ]) ]
    Assert.Equal(AwaitingVerificationOfEffect, (stateOf one).CorrectiveActions[corr 1].State)
    let both = one |> script [ admit 61 "no duplicates in the order table" ] |> attempt (RecordVerification(ver 2, crit 2, Satisfied, [ ev 61 ])) |> stateAfter
    Assert.Equal(VerifiedEffective, both.CorrectiveActions[corr 1].State)

[<Fact>]
let ``[R7.4][A21] a failed verification marks VerificationFailed and returns the investigation to Active`` () =
    let awaiting =
        confirmed
        |> script (implementedCorrection Correction @ [ TransitionCorrection(corr 1, StartVerificationOfEffect); MarkAwaitingVerification; admit 60 "500s persist" ])
    Assert.Equal(AwaitingVerification, (stateOf awaiting).Lifecycle)
    let events, inv = awaiting |> attempt (RecordVerification(ver 1, crit 1, Failed, [ ev 60 ])) |> accepted
    Assert.Equal(VerificationFailed, inv.CorrectiveActions[corr 1].State)
    Assert.Equal(Active, inv.Lifecycle)
    Assert.Equal(Failed, inv.Verifications[ver 1].Outcome)
    Assert.Equal(3, events.Length)
    let reworked = (awaiting @ events) |> script [ TransitionCorrection(corr 1, RequireRework); TransitionCorrection(corr 1, StartCorrection) ] |> stateOf
    Assert.Equal(CorrectionInProgress, reworked.CorrectiveActions[corr 1].State)
    Assert.True(reworked.Verifications.ContainsKey(ver 1)) // the failed attempt remains

[<Fact>]
let ``[R7.4][A35] the resolution gate passes only with a verified correction`` () =
    let blockers = opened |> attempt ResolveInvestigation |> codes
    Assert.Equal<string list>([ "RESOLVE.NO_CONFIRMED_CAUSE"; "RESOLVE.NO_CORRECTION"; "RESOLVE.NO_CONCLUSION" ], blockers)
    confirmed |> script (verifiedCorrection Correction) |> attempt ResolveInvestigation |> refused "RESOLVE.NO_CONCLUSION"
    let inv = resolvable |> attempt ResolveInvestigation |> stateAfter
    Assert.Equal(Resolved, inv.Lifecycle)

// ---------------------------------------------------------------------------
// R8 State-directed behaviour
// ---------------------------------------------------------------------------

let private sampleRefusals () =
    [ opened |> attempt (RecordHypothesis(hyp 1, " ", None))
      [] |> attempt ActivateInvestigation
      opened |> attempt CloseInvestigation
      opened |> attempt (ConfirmCause(cause 9))
      opened |> attempt ResolveInvestigation
      opened |> attempt (TransitionHypothesis(hyp 9, MarkPlausible, "r", []))
      opened |> attempt (AdmitEvidence(ev 1, UrlInput "ftp://x", Log, "s", None))
      opened |> attempt (EstablishNegativeKnowledge(neg 1, "s", "scope", []))
      opened |> attempt MarkAwaitingVerification
      opened |> script [ RecordProposal(prop 1, ProposedUnknown, "q", None) ] |> attemptAs bot (AcceptProposal(prop 1, gid 4001))
      executeAs alice (Some 0L) (RecordHypothesis(hyp 1, "h", None)) opened |> Result.map (fun evs -> evs, stateOf opened) ]
    |> List.collect (function
        | Error errors -> errors
        | Ok _ -> failwith "every sample must be a refusal")

[<Fact>]
let ``[R8.1][R8.2] every refusal carries a non-empty code, rule and message`` () =
    let errors = sampleRefusals ()
    Assert.True(errors.Length >= 10)
    Assert.All(errors, fun e ->
        Assert.False(String.IsNullOrWhiteSpace e.Code)
        Assert.False(String.IsNullOrWhiteSpace e.Rule)
        Assert.False(String.IsNullOrWhiteSpace e.Message))

[<Fact>]
let ``[R8.3][A26] refusals map to Aegis domain faults carrying the same code`` () =
    let errors = sampleRefusals ()
    let faults = Faults.ofDomainErrors "DomainTests" errors
    Assert.Equal(errors.Length, faults.Length)
    List.zip errors faults
    |> List.iter (fun (e, f) ->
        Assert.Equal(Aegis.FaultCode e.Code, f.Code)
        Assert.Equal(Aegis.FailureCategory.DomainFailure, f.Category))

// ---------------------------------------------------------------------------
// R15 AI behaviour: proposals
// ---------------------------------------------------------------------------

let private proposed =
    opened |> scriptAs bot [ RecordProposal(prop 1, ProposedHypothesis, "The parser changed denominator semantics", Some "diff touches the parser") ]

[<Fact>]
let ``[R15.2][A18] an agent statement is recorded as a proposal, not as evidence or a hypothesis`` () =
    let inv = stateOf proposed
    let p = inv.Proposals[prop 1]
    Assert.Equal(ProposalActive, p.State)
    Assert.Equal(bot, p.Provenance.Actor)
    Assert.Equal("The parser changed denominator semantics", p.Text.Value)
    Assert.Empty(inv.Evidence)
    Assert.Empty(inv.Hypotheses)

[<Fact>]
let ``[R15.5][A26] an agent cannot accept a proposal, and an agent ConfirmCause leaves state unchanged`` () =
    proposed |> attemptAs bot (AcceptProposal(prop 1, gid 1050)) |> refused "PROPOSAL.AGENT_CANNOT_ACCEPT"
    let blocked = opened |> script [ RecordHypothesis(hyp 1, "H1", None); RecordCausalAssertion(cause 1, "o", [ memberOf RootCause 1 ], None) ]
    match blocked |> attemptAs bot (ConfirmCause(cause 1)) with
    | Error errors -> Assert.All(Faults.ofDomainErrors "ConfirmCause" errors, fun f -> Assert.Equal(Aegis.FailureCategory.DomainFailure, f.Category))
    | Ok _ -> failwith "the gate must refuse"
    Assert.Equal(CauseProposed, (stateOf blocked).Causes[cause 1].State)

[<Fact>]
let ``[R15.5][A18] a human accepting a proposal runs the target command`` () =
    let events, inv = proposed |> attempt (AcceptProposal(prop 1, gid 1050)) |> accepted
    Assert.Equal<EventBody list>(
        [ HypothesisRecorded(hyp 50, NonEmptyText.tryCreate "The parser changed denominator semantics" |> Option.get, None)
          ProposalWasAccepted(prop 1, gid 1050) ],
        bodies events
    )
    Assert.Equal(Proposed, inv.Hypotheses[hyp 50].State)
    Assert.Equal(alice, inv.Hypotheses[hyp 50].Provenance.Actor)
    Assert.Equal(ProposalAccepted, inv.Proposals[prop 1].State)
    Assert.Empty(inv.Evidence)
    let unknownInv =
        opened |> scriptAs bot [ RecordProposal(prop 2, ProposedUnknown, "Was the cache flushed?", None) ] |> attempt (AcceptProposal(prop 2, gid 4002)) |> stateAfter
    Assert.Equal(Identified, unknownInv.Unknowns[unk 2].State)
    (proposed @ events) |> attempt (AcceptProposal(prop 1, gid 1051)) |> refused "PROPOSAL.NOT_ACTIVE"

[<Fact>]
let ``[R15.5][A19] a rejected proposal is kept and creates no hypothesis`` () =
    let rejected = proposed |> script [ RejectProposal(prop 1, "the parser was not deployed") ]
    let inv = stateOf rejected
    Assert.Equal(ProposalRejected, inv.Proposals[prop 1].State)
    Assert.Equal(Some "the parser was not deployed", inv.Proposals[prop 1].Decision |> Option.map NonEmptyText.value)
    Assert.Empty(inv.Hypotheses)
    rejected |> attempt (AcceptProposal(prop 1, gid 1050)) |> refused "PROPOSAL.NOT_ACTIVE"
    rejected |> attempt (RejectProposal(prop 1, "again")) |> refused "PROPOSAL.NOT_ACTIVE"
    proposed |> attempt (RejectProposal(prop 1, " ")) |> refused "VALIDATION.EMPTY"

[<Fact>]
let ``[R15.5][A26] a blocked target command does not produce ProposalWasAccepted`` () =
    let history = proposed |> script [ RecordHypothesis(hyp 50, "Already here", None) ]
    history |> attempt (AcceptProposal(prop 1, gid 1050)) |> refused "DUPLICATE_ID"
    let inv = stateOf history
    Assert.Equal(ProposalActive, inv.Proposals[prop 1].State)
    Assert.Equal("Already here", inv.Hypotheses[hyp 50].Statement.Value)

// ---------------------------------------------------------------------------
// Concurrency, determinism, frozen resolution
// ---------------------------------------------------------------------------

[<Fact>]
let ``[§16.3][R10.4] a stale ExpectedVersion is refused with VERSION.STALE`` () =
    let version = (stateOf opened).Version
    let command = RecordHypothesis(hyp 1, "H1", None)
    match executeAs alice (Some(version - 1L)) command opened with
    | Error errors -> Assert.Equal<string list>([ "VERSION.STALE" ], codesOf errors)
    | Ok _ -> failwith "a stale write must be refused"
    match executeAs alice (Some(version - 1L)) ResolveInvestigation opened with
    | Error errors -> Assert.Equal<string list>([ "VERSION.STALE" ], codesOf errors)
    | Ok _ -> failwith "a stale lifecycle command must be refused"
    Assert.True(Result.isOk (executeAs alice (Some version) command opened))

[<Fact>]
let ``[A30] replaying the same events twice yields equal projections`` () =
    let incremental = resolvable |> List.fold (fun s e -> Some(Evolve.apply s e)) None
    Assert.Equal(Evolve.replay resolvable, Evolve.replay resolvable)
    Assert.Equal(Evolve.replay resolvable, incremental)
    Assert.Equal((stateOf resolvable).Version, (List.last resolvable).Sequence)

[<Fact>]
let ``[A30] execute stamps contiguous sequences, including multi-event commands`` () =
    Assert.Equal<int64 list>([ 1L .. int64 resolvable.Length ], resolvable |> List.map (fun e -> e.Sequence))
    let awaiting =
        confirmed
        |> script (implementedCorrection Correction @ [ TransitionCorrection(corr 1, StartVerificationOfEffect); MarkAwaitingVerification ])
    let version = (stateOf awaiting).Version
    let events, _ = awaiting |> attempt (RecordVerification(ver 1, crit 1, Failed, [])) |> accepted
    Assert.Equal<int64 list>([ version + 1L; version + 2L; version + 3L ], events |> List.map (fun e -> e.Sequence))
    Assert.All(events, fun e -> Assert.Equal(alice, e.Actor))

[<Fact>]
let ``[A36][D-005] a resolved investigation refuses content changes and reopens only with a basis`` () =
    let resolved = resolvable |> script [ ResolveInvestigation ]
    resolved |> attempt (RecordHypothesis(hyp 9, "H9", None)) |> refused "INVESTIGATION.NOT_OPEN"
    resolved |> attempt (admit 9 "late evidence") |> refused "INVESTIGATION.NOT_OPEN"
    resolved |> attempt ActivateInvestigation |> refused "TRANSITION.ILLEGAL"
    resolved |> attempt (ReopenInvestigation "  ") |> refused "VALIDATION.EMPTY"
    let basis = "Customers report duplicate orders after the fix"
    let events, inv = resolved |> attempt (ReopenInvestigation basis) |> accepted
    Assert.Equal<EventBody list>([ LifecycleChanged(Resolved, Active, "ReopenInvestigation", Some basis) ], bodies events)
    Assert.Equal(Active, inv.Lifecycle)
    Assert.Contains(resolved, fun e -> match e.Body with LifecycleChanged(_, Resolved, _, _) -> true | _ -> false)
    (resolved @ events) |> attempt (admit 9 "new contradictory evidence") |> stateAfter |> ignore
    let closed = resolved |> script [ CloseInvestigation ]
    closed |> attempt (RecordHypothesis(hyp 9, "H9", None)) |> refused "INVESTIGATION.NOT_OPEN"
    Assert.Equal(Active, (closed |> attempt (ReopenInvestigation basis) |> stateAfter).Lifecycle)
