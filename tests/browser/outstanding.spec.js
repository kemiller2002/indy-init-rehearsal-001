// Outstanding work (R8.4) on the workspace and in the interim report (R12.3).
import { expect, test } from "@playwright/test";
import { bridgeErrors, fill, startInvestigation } from "./helpers.js";

test.afterEach(async ({ page }) => {
  expect(await bridgeErrors(page)).toEqual([]);
});

test("[R8.4] [R12.3] outstanding work is listed on the workspace and in the interim report", async ({ page }) => {
  await startInvestigation(page, "Invoices are emailed twice.", "Duplicate invoices");
  await fill(page, "What do we not know yet?", "Does the retry job run on both schedulers?");
  await page.locator("#unk-materiality").selectOption("Blocking");
  await page.getByRole("button", { name: "Add open question" }).click();
  const panel = page.getByTestId("outstanding");
  await expect(panel.getByTestId("outstanding-item").filter({ hasText: "Does the retry job run on both schedulers?" }).first()).toBeVisible();
  await expect(panel).toContainText("No root cause has been confirmed.");
  await page.getByRole("link", { name: "Open report" }).click();
  const report = page.getByTestId("report-outstanding");
  await expect(report).toContainText("Does the retry job run on both schedulers?");
  await expect(report).toContainText("RESOLVE.NO_CONFIRMED_CAUSE");
});
