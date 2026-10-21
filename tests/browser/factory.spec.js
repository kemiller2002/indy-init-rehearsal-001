// Factory proof view (S20/S21): the factory's state at build time.
import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { bridgeErrors, open } from "./helpers.js";

test.afterEach(async ({ page }) => {
  expect(await bridgeErrors(page)).toEqual([]);
});

test("[R14.2] [R14.3] the factory view shows the build commit, requirements with their slice, work-item states and the recorded test counts", async ({ page, request, baseURL }) => {
  const info = await (await request.get(new URL("build-info.json", baseURL).href)).json();
  const factory = await (await request.get(new URL("factory.json", baseURL).href)).json();
  await open(page, "#/factory");
  await expect(page.getByRole("heading", { level: 2, name: "Factory state at build time" })).toBeVisible();
  await expect(page.getByTestId("factory-commit")).toHaveText(info.commit);
  const r81 = page.getByTestId("factory-requirement").filter({ hasText: "IIR-R8.1" });
  await expect(r81).toContainText("SLICE-");
  expect(await page.getByTestId("factory-requirement").count()).toBeGreaterThan(10);
  await expect(page.getByTestId("factory-work-item").filter({ hasText: "SLICE-BOOTSTRAP" })).toBeVisible();
  if (factory.tests !== null) {
    // test-results/traceability.json was present at build time: its real counts are shown.
    await expect(page.getByTestId("factory-tests")).toContainText(`${factory.tests.tests} tests recorded, ${factory.tests.passed} passed, ${factory.tests.failed} failed`);
    await expect(page.getByTestId("factory-build-status")).toContainText(factory.tests.failed === 0 ? "every recorded test passed" : "some recorded tests failed");
  } else {
    await expect(page.getByTestId("factory-tests")).toHaveText("Test results were not available when this site was built.");
    await expect(page.getByTestId("factory-build-status")).toContainText("unknown");
  }
});

test("[R9.3] the factory view has no serious or critical axe violations", async ({ page }) => {
  await open(page, "#/factory");
  await expect(page.getByTestId("factory-commit")).not.toBeEmpty();
  const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  expect(results.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
});
