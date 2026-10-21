// End-to-end investigation journey against the built site. Each test names
// the acceptance criterion it demonstrates (INVESTIGATION-ACCEPTANCE-SCENARIOS).
import { expect, test } from "@playwright/test";
import {
  addEvidence, addHypothesis, bridgeErrors, changeHypothesis, choose, fill, hypothesis, link, open,
  proposeCause, refusal, startInvestigation, stateOf, supportedAndFalsified
} from "./helpers.js";

test.afterEach(async ({ page }) => {
  expect(await bridgeErrors(page)).toEqual([]);
});

test("AC-01 create an investigation from free text; the original observation is preserved verbatim @ipad", async ({ page }) => {
  const observation = "Since Tuesday the nightly export job  sometimes writes an EMPTY file —\nbut only on the second server.";
  await startInvestigation(page, observation, "Empty nightly export");
  await expect(page.getByTestId("observation")).toHaveText(observation, { useInnerText: false });
  await expect(page.getByTestId("local-status")).toHaveText("Saved on this device");
  await expect(page.getByTestId("remote-status")).toHaveText("Local only — not synced to GitHub");
  await page.getByRole("link", { name: "Investigations" }).first().click();
  await expect(page.getByRole("link", { name: "Empty nightly export" })).toBeVisible();
});

test("AC-02 add evidence with its source and capture date; provenance is shown", async ({ page }) => {
  await startInvestigation(page, "Login fails for some users.", "Login failures");
  await fill(page, "Date captured (optional)", "2026-10-20");
  await addEvidence(page, "Auth service log shows 'clock skew too great' for 41 requests", "auth-svc log, pod auth-7", { category: "Log" });
  const item = page.getByTestId("evidence").filter({ hasText: "clock skew" });
  await expect(item).toContainText("Source: auth-svc log, pod auth-7");
  await expect(item).toContainText("Log");
  await expect(item).toContainText("captured 2026-10-20");
  await expect(item).toContainText("Added by You");
  await addEvidence(page, "https://example.com/grafana/d/auth-latency", "Grafana dashboard", { url: true, category: "ExternalReference" });
  await expect(page.getByRole("link", { name: "https://example.com/grafana/d/auth-latency" })).toBeVisible();
});

test("AC-03 two hypotheses: one supported, the other falsified with reason and evidence; the falsified one stays visible @ipad", async ({ page }) => {
  await supportedAndFalsified(page);
  const ruledOut = hypothesis(page, "DNS misconfiguration");
  await expect(ruledOut).toBeVisible();
  await expect(ruledOut).toContainText("Last change: Proposed → Ruled out because: DNS answers are correct everywhere (evidence E2)");
  await expect(page.getByTestId("history-item").filter({ hasText: "changed from Proposed to Ruled out" })).toHaveCount(1);
});

test("AC-04 contradicting evidence is surfaced as a contradiction", async ({ page }) => {
  await startInvestigation(page, "Disk fills up on build agents.", "Build agent disk");
  await addHypothesis(page, "Docker layer cache is never pruned");
  await addEvidence(page, "Prune cron ran successfully every night this week", "cron log", { category: "Log" });
  await link(page, "Prune cron", "Contradicts", "Docker layer cache");
  await expect(page.getByTestId("contradicting-link")).toContainText("contradicts hypothesis H1: Docker layer cache is never pruned");
  await expect(hypothesis(page, "Docker layer cache")).toContainText("Contradicted by: E1");
  await fill(page, "Record a contradiction", "Cache is pruned nightly yet disk still fills");
  await choose(page, "Evidence involved", "Prune cron");
  await page.getByRole("button", { name: "Record contradiction" }).click();
  await expect(page.getByTestId("contradiction")).toContainText("Cache is pruned nightly yet disk still fills");
  await expect(page.getByTestId("contradiction")).toContainText("Open");
});

test("AC-05 causal assertion with a root cause and a contributing factor", async ({ page }) => {
  await supportedAndFalsified(page);
  await addHypothesis(page, "Certificate expiry alerts were muted");
  await fill(page, "Problem being explained", "Checkout 502 errors");
  await choose(page, "Root cause", "Expired TLS");
  await choose(page, "Contributing factor (optional)", "alerts were muted");
  await fill(page, "How it causes the problem (mechanism)", "Expired certificate fails handshakes; muted alerts hid the expiry.");
  await page.getByRole("button", { name: "Propose cause" }).click();
  const cause = page.getByTestId("cause");
  await expect(cause).toContainText("Root cause: H1: Expired TLS certificate on the load balancer");
  await expect(cause).toContainText("Contributing factor: H3: Certificate expiry alerts were muted");
});

test("AC-06 the legal actions for each hypothesis are listed, and unavailable ones explained @ipad", async ({ page }) => {
  await startInvestigation(page, "API latency doubled.", "API latency");
  await addHypothesis(page, "Connection pool exhausted");
  const card = hypothesis(page, "Connection pool exhausted");
  // Proposed: the kernel table allows plausible, falsify and replace only.
  await expect(card.getByRole("button", { name: "Mark plausible" })).toBeVisible();
  await expect(card.getByRole("button", { name: "Rule out (falsify)" })).toBeVisible();
  await expect(card.getByRole("button", { name: "Mark replaced" })).toBeVisible();
  await expect(card.getByRole("button", { name: "Mark supported" })).toBeHidden();
  await expect(card.getByRole("button", { name: "Reopen" })).toBeHidden();
  await expect(card).toContainText("Mark supported (only when Plausible or Weakened)");
  await changeHypothesis(page, "Connection pool exhausted", "Mark plausible", "Pool metrics are at the limit");
  await expect(card.getByRole("button", { name: "Mark supported" })).toBeVisible();
  await expect(card.getByRole("button", { name: "Mark plausible" })).toBeHidden();
});

test("AC-07 confirming a cause is blocked with reasons while a blocking unknown is open, then allowed once it is answered", async ({ page }) => {
  await supportedAndFalsified(page);
  await fill(page, "What do we not know yet?", "Was the certificate renewed on every load balancer?");
  await page.locator("#unk-materiality").selectOption("Blocking");
  await page.getByRole("button", { name: "Add open question" }).click();
  await expect(page.getByTestId("unknown")).toContainText("Blocking");
  await proposeCause(page);
  await expect(page.getByTestId("cause-readiness")).toContainText("Cannot be confirmed yet");
  await expect(page.getByTestId("cause-blocker").filter({ hasText: "CAUSE.OPEN_UNKNOWN" })).toContainText("Was the certificate renewed");
  // Policy.canConfirm is false: the action is not offered, and the reasons are listed.
  await expect(page.getByRole("button", { name: "Confirm root cause" })).toBeHidden();
  await fill(page, "Answer, or why it is accepted as remaining uncertainty", "All three load balancers carry the same expired certificate");
  await page.getByTestId("unknown").getByRole("button", { name: "Mark answered" }).click();
  await expect(page.getByTestId("unknown")).toContainText("Answered");
  await expect(page.getByTestId("cause-readiness")).toHaveText("Ready to confirm: every condition is met.");
  await page.getByRole("button", { name: "Confirm root cause" }).click();
  await expect(page.getByTestId("cause")).toContainText("Confirmed");
  await expect(refusal(page)).toHaveCount(0);
});

test("AC-07 an illegal action is refused with its reason and Aegis fault code", async ({ page }) => {
  await startInvestigation(page, "Emails are delayed.", "Delayed emails");
  await addHypothesis(page, "Mail queue worker is stuck");
  // Ruling out requires cited evidence (R2.3): no evidence chosen.
  await changeHypothesis(page, "Mail queue worker", "Rule out (falsify)", "Worker looks healthy");
  await expect(refusal(page)).toContainText("Falsify must cite the evidence that caused it.");
  await expect(refusal(page)).toContainText("HYPOTHESIS.EVIDENCE_REQUIRED");
  await expect(refusal(page)).toContainText("rule R2.3");
  await expect(refusal(page)).toContainText("Aegis fault HYPOTHESIS.EVIDENCE_REQUIRED");
  await expect(stateOf(page, "Mail queue worker")).toHaveText("Proposed");
});

test("AC-08 [R10.7] reload preserves the full investigation state at the same deep link", async ({ page }) => {
  await supportedAndFalsified(page);
  await proposeCause(page);
  const url = page.url();
  const historyCount = await page.getByTestId("history-item").count();
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page).toHaveURL(url);
  await expect(page.getByRole("heading", { level: 1, name: "Checkout 502 errors" })).toBeVisible();
  await expect(stateOf(page, "Expired TLS")).toHaveText("Supported by evidence");
  await expect(stateOf(page, "DNS misconfiguration")).toHaveText("Ruled out");
  await expect(page.getByTestId("evidence")).toHaveCount(2);
  await expect(page.getByTestId("cause")).toHaveCount(1);
  await expect(page.getByTestId("history-item")).toHaveCount(historyCount);
});

test("[R9.8] deep link to an unknown investigation id shows not found with a way home", async ({ page }) => {
  await open(page, "#/i/0b0e8d7c-1111-4222-8333-944455556666");
  await expect(page.getByRole("heading", { level: 1, name: "Not found" })).toBeVisible();
  await expect(page.getByText("This investigation is not on this device.")).toBeVisible();
  await page.getByRole("link", { name: "Go to your investigations" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Investigations" })).toBeVisible();
  await open(page, "#/no/such/page");
  await expect(page.getByRole("heading", { level: 1, name: "Not found" })).toBeVisible();
});

test("AC-11 resolve is blocked until the correction has verification evidence; AC-10 interim then final report", async ({ page }) => {
  await supportedAndFalsified(page);
  await proposeCause(page);
  await page.getByRole("button", { name: "Confirm root cause" }).click();
  await expect(page.getByTestId("cause")).toContainText("Confirmed");

  await page.getByLabel("Kind of fix", { exact: true }).selectOption("Correction");
  await fill(page, "What will be done", "Renew the certificate and add expiry monitoring");
  await choose(page, "Cause it addresses", "C1");
  await page.getByRole("button", { name: "Add fix" }).click();
  const fix = page.getByTestId("correction");
  await expect(fix).toContainText("Root-cause correction");
  await fix.getByRole("button", { name: "Approve" }).click();
  await fix.getByRole("button", { name: "Start work" }).click();
  await fix.getByRole("button", { name: "Mark implemented" }).click();
  await expect(fix.getByTestId("correction-state")).toHaveText("Implemented (not yet verified)");
  await choose(page, "Fix to check", "A1");
  await fill(page, "Success looks like", "No 502 responses for 24 hours");
  await page.getByRole("button", { name: "Add success criterion" }).click();
  await fix.getByRole("button", { name: "Start verification" }).click();
  await expect(fix.getByTestId("correction-state")).toHaveText("Waiting for verification");

  // Blocked: implemented is not verified (R7.4), and there is no conclusion.
  await expect(page.getByTestId("resolve-blocker").filter({ hasText: "RESOLVE.CORRECTION_NOT_VERIFIED" })).toBeVisible();
  await page.getByRole("button", { name: "Resolve investigation" }).click();
  await expect(refusal(page).filter({ hasText: "RESOLVE.CORRECTION_NOT_VERIFIED" })).toBeVisible();

  // AC-10 interim report names the unresolved work.
  await page.getByRole("link", { name: "Open report" }).click();
  await expect(page.getByTestId("report-kind")).toHaveText("Interim report — work remains unresolved");
  await expect(page.getByTestId("report-unresolved")).toContainText("RESOLVE.CORRECTION_NOT_VERIFIED");
  await expect(page.getByTestId("report-digest")).toHaveText(/^sha256:[0-9a-f]{64}$/);
  await expect(page.locator("ef-print-document")).toContainText("Expired TLS certificate on the load balancer");
  await page.getByRole("link", { name: "Back to the investigation" }).click();

  // Verification with evidence, conclusion, resolve.
  await addEvidence(page, "No 502 responses in the 24 hours after renewal", "load balancer access logs", { category: "Log" });
  await choose(page, "Criterion verified", "K1");
  await page.getByLabel("Result", { exact: true }).selectOption("Satisfied");
  await choose(page, "Verification evidence", "No 502 responses");
  await page.getByRole("button", { name: "Record verification" }).click();
  await expect(fix.getByTestId("correction-state")).toHaveText("Verified effective");
  await fill(page, "Conclusion", "The expired load balancer certificate caused the 502 errors");
  await page.getByLabel("How strong", { exact: true }).selectOption("ConfirmedCause");
  await choose(page, "Cause concluded", "C1");
  await fill(page, "Remaining uncertainty", "Why monitoring missed the expiry is tracked separately");
  await page.getByRole("button", { name: "Record conclusion" }).click();
  await expect(page.getByTestId("resolve-panel")).toContainText("Ready to resolve");
  await page.getByRole("button", { name: "Resolve investigation" }).click();
  await expect(page.getByTestId("resolve-panel")).toContainText("This investigation is Resolved.");

  await page.getByRole("link", { name: "Open report" }).click();
  await expect(page.getByTestId("report-kind")).toHaveText("Final report");
  await expect(page.getByTestId("report-unresolved")).toHaveCount(0);
  await expect(page.locator("ef-print-document")).toContainText("Renew the certificate and add expiry monitoring");
});

test("[R15.2][R15.5] AI-suggested hypothesis is recorded as the AI's and accepted only by the person", async ({ page }) => {
  await startInvestigation(page, "Search results are stale.", "Stale search");
  await fill(page, "Add AI-suggested hypothesis", "The indexer job stopped after the last deploy");
  await page.getByRole("button", { name: "Record AI suggestion" }).click();
  const proposal = page.getByTestId("proposal");
  await expect(proposal).toContainText("AI suggestion");
  await expect(proposal).toContainText("Waiting for your decision");
  await expect(page.getByTestId("hypothesis")).toHaveCount(0);
  await proposal.getByRole("button", { name: "Accept as hypothesis" }).click();
  await expect(hypothesis(page, "The indexer job stopped")).toBeVisible();
  await expect(proposal).toContainText("Accepted");
  await expect(page.getByTestId("history-item").filter({ hasText: "AI suggestion recorded" })).toContainText("AI suggestion");
});

test("[R9.6] a deep link pasted into a new tab of the same browser opens the same investigation", async ({ page, context }) => {
  await supportedAndFalsified(page);
  const url = page.url();
  const other = await context.newPage();
  await other.goto(url);
  await expect(other.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(other.getByRole("heading", { level: 1, name: "Checkout 502 errors" })).toBeVisible();
  await expect(stateOf(other, "DNS misconfiguration")).toHaveText("Ruled out");
  await other.close();
});
