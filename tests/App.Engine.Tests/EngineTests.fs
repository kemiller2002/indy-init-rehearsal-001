module App.Engine.Tests.EngineTests

open Xunit
open IndyInitRootCauseInvestigator.Engine

[<Fact>]
let ``the Aegis configuration validates at startup`` () =
    Assert.True(Result.isOk (Operational.validateConfiguration ()))

[<Fact>]
let ``a domain refusal maps to an Aegis domain fault with the rule in context`` () =
    let fault = Faults.ofDomainError "ConfirmCause" (DomainError.create "CAUSE.OPEN_UNKNOWN" "§15.5" "Unknown is unresolved.")
    Assert.Equal(Aegis.FaultCode "CAUSE.OPEN_UNKNOWN", fault.Code)
    Assert.Equal(Aegis.FailureCategory.DomainFailure, fault.Category)
