// Factory proof view (S20/S21): the factory's state at build time.
import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { bridgeErrors, open } from "./helpers.js";

test.afterEach(async ({ page }) => {
  expect(await bridgeErrors(page)).toEqual([]);
});

test("[R14.2] [R14.3] [R16.5] the factory view shows the build commit, requirements with their slice, and work-item states", async ({ page, request, baseURL }) => {
  const info = await (await request.get(new URL("build-info.json", baseURL).href)).json();
  await open(page, "#/factory");
  await expect(page.getByRole("heading", { level: 2, name: "Factory state at build time" })).toBeVisible();
  await expect(page.getByTestId("factory-commit")).toHaveText(info.commit);
  const r81 = page.getByTestId("factory-requirement").filter({ hasText: "IIR-R8.1" });
  await expect(r81).toContainText("SLICE-");
  expect(await page.getByTestId("factory-requirement").count()).toBeGreaterThan(10);
  await expect(page.getByTestId("factory-work-item").filter({ hasText: "SLICE-BOOTSTRAP" })).toBeVisible();
  await expect(page.getByTestId("factory-tests")).not.toBeEmpty();
});

test("[R9.3] the factory view has no serious or critical axe violations", async ({ page }) => {
  await open(page, "#/factory");
  await expect(page.getByTestId("factory-commit")).not.toBeEmpty();
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  expect(results.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
});
