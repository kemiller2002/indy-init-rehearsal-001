// Rule-based challenge helper (not AI) and evidence-linked posture.
import { expect, test } from "@playwright/test";
import { addEvidence, addHypothesis, bridgeErrors, changeHypothesis, link, startInvestigation } from "./helpers.js";

test.afterEach(async ({ page }) => {
  expect(await bridgeErrors(page)).toEqual([]);
});

test("[R15.3] [R6.2] the labelled rule-based helper challenges a supported explanation; posture is explained by evidence", async ({ page }) => {
  await startInvestigation(page, "Mobile app crashes on launch for some users.", "Launch crashes");
  await addHypothesis(page, "A null font asset on older Android versions");
  await addHypothesis(page, "Corrupted local cache after update");
  await addEvidence(page, "Crash logs show NullPointerException in FontLoader on Android 9 only", "crash reporter", { category: "Log" });
  await link(page, "Crash logs show", "Supports", "null font asset");
  await changeHypothesis(page, "null font asset", "Mark plausible", "Matches crash stack");
  await changeHypothesis(page, "null font asset", "Mark supported", "FontLoader NPE on Android 9", "Crash logs show");

  const challenge = page.getByTestId("challenge");
  await expect(challenge).toContainText("A rule-based helper, not AI");
  await challenge.getByRole("button", { name: "Challenge the leading explanation" }).click();
  const proposal = page.getByTestId("proposal");
  await expect(proposal).toContainText("Rule-based helper (not AI)");
  await expect(proposal).toContainText("What result would falsify \"A null font asset on older Android versions\"?");
  await expect(challenge.getByRole("button", { name: "Challenge the leading explanation" })).toBeHidden();
  await proposal.getByRole("button", { name: "Accept as planned check" }).click();
  await expect(page.getByTestId("action")).toContainText("What result would falsify");

  const posture = page.getByTestId("hypothesis").filter({ hasText: "null font asset" }).getByTestId("posture");
  await expect(posture).toContainText("1 supporting (E1)");
  await expect(posture).not.toContainText("%");
});
