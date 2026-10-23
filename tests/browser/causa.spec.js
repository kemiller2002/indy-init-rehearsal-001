// CAUSA-REHEARSAL-INTEGRATION-001: the frozen Causa storage release (causa-data@causa-storage-v1.0.0) in the real
// page. The data is the vendored release bytes the build ships; nothing here is a reconstructed copy.
import { expect, test } from "@playwright/test";
import { bridgeErrors, open } from "./helpers.js";

const frozenTitles = [
  "Card payments failing at checkout",
  "Error-rate dashboard contradicts application logs",
  "Intermittent 502 errors on the order-status API",
  "Users unable to sign in via single sign-on",
  "p95 latency regression on the search API after release 4.2",
];

test("Causa: all five frozen investigations load, verify and are listed with their summaries @ipad", async ({ page }) => {
  await open(page);
  await expect(page.getByTestId("demo-card")).toHaveCount(5);
  await expect(page.getByTestId("demo-summary")).toContainText("5 investigation(s) from causa-storage-v1.0.0");
  const titles = await page.getByTestId("demo-card").locator("h3").allTextContents();
  expect([...titles].sort()).toEqual([...frozenTitles].sort());
  await expect(page.getByTestId("demo-rejected")).toHaveCount(0);
  expect(await bridgeErrors(page)).toEqual([]);
});
