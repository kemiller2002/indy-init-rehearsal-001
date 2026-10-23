namespace IndyInitRootCauseInvestigator.Engine

open System
open System.Collections.Generic
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

/// Hand-written, stable persisted names for every domain union case.
/// No reflection (`%A`, `ToString` on unions) is used: the engine runs trimmed.
[<RequireQualifiedAccess>]
module CaseNames =
    let lifecycle =
        function
        | Draft -> "Draft"
        | Active -> "Active"
        | AwaitingEvidence -> "AwaitingEvidence"
        | AwaitingVerification -> "AwaitingVerification"
        | Resolved -> "Resolved"
        | Closed -> "Closed"
        | Archived -> "Archived"

    let allLifecycles = [ Draft; Active; AwaitingEvidence; AwaitingVerification; Resolved; Closed; Archived ]

    let hypothesisState =
        function
        | Proposed -> "Proposed"
        | Plausible -> "Plausible"
        | Supported -> "Supported"
        | Weakened -> "Weakened"
        | Falsified -> "Falsified"
        | Superseded -> "Superseded"

    let allHypothesisStates = [ Proposed; Plausible; Supported; Weakened; Falsified; Superseded ]

    let unknownState =
        function
        | Identified -> "Identified"
        | Investigating -> "Investigating"
        | UnknownResolved -> "UnknownResolved"
        | AcceptedResidual -> "AcceptedResidual"
        | UnknownSuperseded -> "UnknownSuperseded"

    let allUnknownStates = [ Identified; Investigating; UnknownResolved; AcceptedResidual; UnknownSuperseded ]

    let materiality =
        function
        | Blocking -> "Blocking"
        | Material -> "Material"
        | NonMaterial -> "NonMaterial"
        | Informational -> "Informational"

    let allMaterialities = [ Blocking; Material; NonMaterial; Informational ]

    let assumptionState =
        function
        | ExplicitUnverified -> "ExplicitUnverified"
        | ProvisionallyAccepted -> "ProvisionallyAccepted"
        | AssumptionVerified -> "AssumptionVerified"
        | Rejected -> "Rejected"
        | Expired -> "Expired"

    let allAssumptionStates = [ ExplicitUnverified; ProvisionallyAccepted; AssumptionVerified; Rejected; Expired ]

    let contradictionState =
        function
        | Detected -> "Detected"
        | Triaged -> "Triaged"
        | ContradictionInvestigating -> "ContradictionInvestigating"
        | ContradictionResolved -> "ContradictionResolved"
        | ContradictionAcceptedResidual -> "ContradictionAcceptedResidual"
        | ContradictionSuperseded -> "ContradictionSuperseded"

    let allContradictionStates =
        [ Detected; Triaged; ContradictionInvestigating; ContradictionResolved; ContradictionAcceptedResidual; ContradictionSuperseded ]

    let actionState =
        function
        | ActionProposed -> "ActionProposed"
        | ActionApproved -> "ActionApproved"
        | ActionInProgress -> "ActionInProgress"
        | ActionCompleted -> "ActionCompleted"
        | ActionInterpreted -> "ActionInterpreted"
        | ActionRejected -> "ActionRejected"
        | ActionCancelled -> "ActionCancelled"

    let allActionStates =
        [ ActionProposed; ActionApproved; ActionInProgress; ActionCompleted; ActionInterpreted; ActionRejected; ActionCancelled ]

    let actionKind =
        function
        | Test -> "Test"
        | Inspection -> "Inspection"
        | Query -> "Query"
        | ActionKind.Comparison -> "Comparison"
        | Experiment -> "Experiment"

    let allActionKinds = [ Test; Inspection; Query; ActionKind.Comparison; Experiment ]

    let causeState =
        function
        | CauseProposed -> "CauseProposed"
        | CauseConfirmed -> "CauseConfirmed"
        | CauseReopened -> "CauseReopened"
        | CauseSuperseded -> "CauseSuperseded"

    let allCauseStates = [ CauseProposed; CauseConfirmed; CauseReopened; CauseSuperseded ]

    let causeRole =
        function
        | RootCause -> "RootCause"
        | ContributingFactor -> "ContributingFactor"
        | Trigger -> "Trigger"
        | EnablingCondition -> "EnablingCondition"

    let allCauseRoles = [ RootCause; ContributingFactor; Trigger; EnablingCondition ]

    let correctionState =
        function
        | CorrectionProposed -> "CorrectionProposed"
        | CorrectionApproved -> "CorrectionApproved"
        | CorrectionInProgress -> "CorrectionInProgress"
        | Implemented -> "Implemented"
        | AwaitingVerificationOfEffect -> "AwaitingVerificationOfEffect"
        | VerifiedEffective -> "VerifiedEffective"
        | VerificationFailed -> "VerificationFailed"
        | ReworkRequired -> "ReworkRequired"
        | CorrectionCancelled -> "CorrectionCancelled"

    let allCorrectionStates =
        [ CorrectionProposed; CorrectionApproved; CorrectionInProgress; Implemented; AwaitingVerificationOfEffect
          VerifiedEffective; VerificationFailed; ReworkRequired; CorrectionCancelled ]

    let correctionKind =
        function
        | Mitigation -> "Mitigation"
        | Workaround -> "Workaround"
        | Correction -> "Correction"
        | Prevention -> "Prevention"

    let allCorrectionKinds = [ Mitigation; Workaround; Correction; Prevention ]

    let evidenceCategory =
        function
        | DirectObservation -> "DirectObservation"
        | Measurement -> "Measurement"
        | Log -> "Log"
        | Document -> "Document"
        | ExperimentResult -> "ExperimentResult"
        | Testimony -> "Testimony"
        | ExternalReference -> "ExternalReference"

    let allEvidenceCategories = [ DirectObservation; Measurement; Log; Document; ExperimentResult; Testimony; ExternalReference ]

    let verificationStatus =
        function
        | Unverified -> "Unverified"
        | PartiallyVerified -> "PartiallyVerified"
        | Verified -> "Verified"
        | Invalidated -> "Invalidated"

    let allVerificationStatuses = [ Unverified; PartiallyVerified; Verified; Invalidated ]

    let relationshipKind =
        function
        | Supports -> "Supports"
        | Contradicts -> "Contradicts"
        | Discriminates -> "Discriminates"
        | Insufficient -> "Insufficient"

    let allRelationshipKinds = [ Supports; Contradicts; Discriminates; Insufficient ]

    let predictionStrength =
        function
        | Necessary -> "Necessary"
        | Expected -> "Expected"
        | DiscriminatingPrediction -> "DiscriminatingPrediction"
        | NonDiscriminating -> "NonDiscriminating"

    let allPredictionStrengths = [ Necessary; Expected; DiscriminatingPrediction; NonDiscriminating ]

    let predictionTiming =
        function
        | Prospective -> "Prospective"
        | Retrospective -> "Retrospective"

    let allPredictionTimings = [ Prospective; Retrospective ]

    let verificationOutcome =
        function
        | Satisfied -> "Satisfied"
        | Failed -> "Failed"
        | Inconclusive -> "Inconclusive"

    let allVerificationOutcomes = [ Satisfied; Failed; Inconclusive ]

    let conclusionStrength =
        function
        | PlausibleExplanation -> "PlausibleExplanation"
        | LeadingExplanation -> "LeadingExplanation"
        | SupportedCause -> "SupportedCause"
        | ConfirmedCause -> "ConfirmedCause"
        | AcceptedWithResidualUncertainty -> "AcceptedWithResidualUncertainty"

    let allConclusionStrengths =
        [ PlausibleExplanation; LeadingExplanation; SupportedCause; ConfirmedCause; AcceptedWithResidualUncertainty ]

    let proposalKind =
        function
        | ProposedHypothesis -> "ProposedHypothesis"
        | ProposedUnknown -> "ProposedUnknown"
        | ProposedAction -> "ProposedAction"

    let allProposalKinds = [ ProposedHypothesis; ProposedUnknown; ProposedAction ]

    let proposalState =
        function
        | ProposalActive -> "ProposalActive"
        | ProposalAccepted -> "ProposalAccepted"
        | ProposalRejected -> "ProposalRejected"

    /// The persisted "type" of an event: the EventBody case name.
    let eventType =
        function
        | InvestigationStarted _ -> "InvestigationStarted"
        | LifecycleChanged _ -> "LifecycleChanged"
        | ObservationRecorded _ -> "ObservationRecorded"
        | HypothesisRecorded _ -> "HypothesisRecorded"
        | HypothesisTransitioned _ -> "HypothesisTransitioned"
        | EvidenceAdmitted _ -> "EvidenceAdmitted"
        | EvidenceSuperseded _ -> "EvidenceSuperseded"
        | EvidenceVerificationAssessed _ -> "EvidenceVerificationAssessed"
        | EvidenceRelated _ -> "EvidenceRelated"
        | UnknownIdentified _ -> "UnknownIdentified"
        | UnknownTransitioned _ -> "UnknownTransitioned"
        | AssumptionRecorded _ -> "AssumptionRecorded"
        | AssumptionTransitioned _ -> "AssumptionTransitioned"
        | ContradictionRecorded _ -> "ContradictionRecorded"
        | ContradictionTransitioned _ -> "ContradictionTransitioned"
        | PredictionRecorded _ -> "PredictionRecorded"
        | ActionRecorded _ -> "ActionRecorded"
        | ActionTransitioned _ -> "ActionTransitioned"
        | FindingRecorded _ -> "FindingRecorded"
        | NegativeKnowledgeEstablished _ -> "NegativeKnowledgeEstablished"
        | CausalAssertionRecorded _ -> "CausalAssertionRecorded"
        | CausalAssertionConfirmed _ -> "CausalAssertionConfirmed"
        | CausalAssertionReopened _ -> "CausalAssertionReopened"
        | CorrectiveActionRecorded _ -> "CorrectiveActionRecorded"
        | CorrectiveActionTransitioned _ -> "CorrectiveActionTransitioned"
        | CriterionRecorded _ -> "CriterionRecorded"
        | VerificationRecorded _ -> "VerificationRecorded"
        | ConclusionRecorded _ -> "ConclusionRecorded"
        | ProposalRecorded _ -> "ProposalRecorded"
        | ProposalWasAccepted _ -> "ProposalWasAccepted"
        | ProposalWasRejected _ -> "ProposalWasRejected"

    /// "AwaitingVerificationOfEffect" -> "Awaiting verification of effect".
    let humanize (name: string) =
        name
        |> Seq.mapi (fun i c -> if i > 0 && Char.IsUpper c then " " + string (Char.ToLowerInvariant c) else string c)
        |> String.concat ""

/// Low-level JSON building/reading over System.Text.Json.Nodes (no reflection).
