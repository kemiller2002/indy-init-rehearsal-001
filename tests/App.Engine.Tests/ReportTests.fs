module App.Engine.Tests.ReportTests

open Xunit
open IndyInitRootCauseInvestigator.Engine
open App.Engine.Tests.CodecTests

let private reportOf events = Report.build (Scenario.replay events) events

let private section id (report: ReportModel) =
    report.Sections |> List.find (fun s -> s.Id = id)

let private itemFor (heading: string) (s: ReportSection) =
    s.Items |> List.find (fun i -> i.Heading = heading)

let private expectedSections =
    [ "timeline"; "hypotheses"; "evidence"; "relationships"; "experiments"; "unknowns"; "negative-knowledge"; "causes"
      "corrective-actions"; "conclusion" ]

[<Fact>]
let ``R12.2 a final report covers every required section`` () =
    let report = reportOf Scenario.closedHistory
    Assert.Equal<string list>(expectedSections, report.Sections |> List.map (fun s -> s.Id))
    Assert.Equal("Pump P-7 trip", report.Title)
    Assert.Equal(Id.text Scenario.iid, report.InvestigationId)
    Assert.Equal(Scenario.tricky, report.Observation)
    Assert.Equal((List.last Scenario.closedHistory).Sequence, report.Version)

[<Fact>]
let ``R12.2 a resolved or closed investigation produces a Final report with no unresolved work`` () =
    let report = reportOf Scenario.closedHistory
    Assert.Equal("Final", report.Kind)
    Assert.Empty(report.UnresolvedWork)

[<Fact>]
let ``R12.3 an open investigation produces an Interim report listing unresolved work`` () =
    let report = reportOf Scenario.interimHistory
    Assert.Equal("Interim", report.Kind)
    let codes = report.UnresolvedWork |> List.map (fun i -> i.Status)
    Assert.Contains("RESOLVE.NO_CONFIRMED_CAUSE", codes)
    Assert.Contains("RESOLVE.NO_CONCLUSION", codes)
    let expected = Policy.resolutionBlockers (Scenario.replay Scenario.interimHistory)
    Assert.Equal(expected.Length, report.UnresolvedWork.Length)
    Assert.Equal("No conclusion recorded", ((section "conclusion" report).Items |> List.exactlyOne).Heading)

[<Fact>]
let ``R12.3 a reopened investigation is Interim again`` () =
    let report = reportOf Scenario.fullHistory
    Assert.Equal("Interim", report.Kind)
    Assert.NotEmpty(report.UnresolvedWork)

[<Fact>]
let ``R12.2 falsified hypotheses are reported with their transition reasons`` () =
    let hypotheses = section "hypotheses" (reportOf Scenario.closedHistory)
    let falsified = itemFor "Supply voltage sag" hypotheses
    Assert.Equal("Falsified", falsified.Status)
    Assert.Contains("Voltage log shows no sag", falsified.Detail)
    Assert.Contains("Proposed → Falsified", falsified.Detail)
    Assert.Equal("Supported", (itemFor "Bearing wear raised motor load" hypotheses).Status)
    // All hypotheses, in the order they were recorded (the accepted AI proposal last).
    Assert.Equal<string list>(
        [ "Bearing wear raised motor load"; "Supply voltage sag"; "Clogged intake strainer"; "Motor winding insulation degraded" ],
        hypotheses.Items |> List.map (fun i -> i.Heading)
    )

[<Fact>]
let ``R12.2 the timeline has one human-readable item per event in order`` () =
    let timeline = section "timeline" (reportOf Scenario.closedHistory)
    Assert.Equal(Scenario.closedHistory.Length, timeline.Items.Length)
    Assert.StartsWith("1. Investigation started", timeline.Items.Head.Heading)
    Assert.Contains("kim (human)", timeline.Items.Head.Status)
    Assert.Contains(timeline.Items, fun i -> i.Status.Contains "AI agent anthropic/claude-opus-5-5")

[<Fact>]
let ``R7.2 R12.2 mitigation and correction are labelled distinctly with verification`` () =
    let actions = section "corrective-actions" (reportOf Scenario.closedHistory)
    let mitigation = itemFor "Reduce duty cycle to 60%" actions
    let correction = itemFor "Replace the drive-end bearing" actions
    Assert.Contains("Mitigation (temporary; does not correct the root cause)", mitigation.Detail)
    Assert.Contains("Correction (root-cause correction)", correction.Detail)
    Assert.Equal("Verified effective", correction.Status)
    Assert.Contains("Vibration below 4 mm/s for 24 h", correction.Detail)
    Assert.Contains("verification Satisfied (evidence: Vibration 2.1 mm/s over 24 h)", correction.Detail)

[<Fact>]
let ``R6.3 R12.2 causes distinguish root cause from contributing factor`` () =
    let cause = (section "causes" (reportOf Scenario.closedHistory)).Items |> List.exactlyOne
    Assert.Contains("Root cause: Bearing wear raised motor load", cause.Detail)
    Assert.Contains("Contributing factor: Clogged intake strainer", cause.Detail)
    Assert.Equal("Cause confirmed", cause.Status)

[<Fact>]
let ``R12.2 evidence, experiments, unknowns, negative knowledge and conclusion are reported`` () =
    let report = reportOf Scenario.closedHistory
    let superseded = itemFor "https://example.org/logs/voltage.csv" (section "evidence" report)
    Assert.Equal("Superseded", superseded.Status)
    Assert.Contains("Superseded by: https://example.org/logs/voltage-v2.csv", superseded.Detail)
    Assert.Contains((section "experiments" report).Items, fun i -> i.Heading = "Finding: Outer race pitting observed")
    Assert.Contains((section "experiments" report).Items, fun i -> i.Heading.StartsWith "Prediction:")
    let unknowns = section "unknowns" report
    Assert.Equal("Unknown resolved", (itemFor "Unknown: Was the lubrication schedule followed?" unknowns).Status)
    Assert.Contains(unknowns.Items, fun i -> i.Heading.StartsWith "Assumption:")
    Assert.Contains(unknowns.Items, fun i -> i.Heading.StartsWith "Contradiction:")
    Assert.Equal("Supply voltage did not sag", ((section "negative-knowledge" report).Items |> List.exactlyOne).Heading)
    let conclusion = (section "conclusion" report).Items |> List.exactlyOne
    Assert.Contains("Residual uncertainty: Why lubrication was skipped is not established", conclusion.Detail)
    Assert.Equal(1, (section "relationships" report).Items |> List.filter (fun i -> i.Status = "Contradicts") |> List.length)

[<Fact>]
let ``A31 repeated generation of the same snapshot is identical`` () =
    let a = reportOf Scenario.closedHistory
    let b = reportOf Scenario.closedHistory
    Assert.Equal(a, b)
    Assert.Equal(EventCodec.digest (EventCodec.encode Scenario.iid Scenario.closedHistory), a.SnapshotDigest)
    Assert.NotEqual<string>(a.SnapshotDigest, (reportOf Scenario.interimHistory).SnapshotDigest)
    // A report rebuilt from the persisted document is the same report.
    let _, decoded = EventCodec.decode (EventCodec.encode Scenario.iid Scenario.closedHistory) |> Result.defaultWith failwith
    Assert.Equal(a, reportOf decoded)
