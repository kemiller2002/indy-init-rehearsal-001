// AC-14 accessibility: axe-core finds no serious or critical violations on
// home, workspace and report, and an investigation can be created by keyboard.
import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { addEvidence, addHypothesis, open, startInvestigation } from "./helpers.js";

const seriousViolations = async (page) => {
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  return results.violations
    .filter((v) => v.impact === "serious" || v.impact === "critical")
    .map((v) => `${v.id} (${v.impact}): ${v.nodes.map((n) => n.target.join(" ") + " " + (n.any[0]?.message ?? "")).slice(0, 2).join(" | ")}`);
};

test("AC-14 home, workspace and report have no serious or critical axe violations @ipad", async ({ page }) => {
  await open(page);
  expect(await seriousViolations(page)).toEqual([]);
  await startInvestigation(page, "Thumbnails fail to render for PNG uploads.", "PNG thumbnails");
  await addHypothesis(page, "Image library upgrade dropped PNG support");
  await addEvidence(page, "Thumbnailer log: unsupported format image/png", "thumbnailer log");
  expect(await seriousViolations(page)).toEqual([]);
  await page.getByRole("link", { name: "Open report" }).click();
  await expect(page.getByTestId("report-kind")).toBeVisible();
  expect(await seriousViolations(page)).toEqual([]);
});

test("AC-14 an investigation can be created with the keyboard alone", async ({ page }) => {
  await open(page);
  // Skip link first, then header links, then the form fields.
  await page.keyboard.press("Tab");
  await expect(page.getByRole("link", { name: "Skip to main content" })).toBeFocused();
  const observation = page.getByLabel("What did you observe?", { exact: true });
  for (let i = 0; i < 10 && !(await observation.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press("Tab");
  await expect(observation).toBeFocused();
  await page.keyboard.type("Queue consumers stop after a broker failover.");
  await page.keyboard.press("Tab");
  await expect(page.getByLabel("Short title (optional)", { exact: true })).toBeFocused();
  await page.keyboard.type("Consumers stop after failover");
  await page.keyboard.press("Tab");
  await expect(page.getByRole("button", { name: "Start investigation" })).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { level: 1, name: "Consumers stop after failover" })).toBeVisible();
  await expect(page.getByTestId("observation")).toHaveText("Queue consumers stop after a broker failover.");
});
