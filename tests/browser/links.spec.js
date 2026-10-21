// Addressable entities and share links (R9.6, R9.8, R9.9, R9.10).
import { expect, test } from "@playwright/test";
import { addHypothesis, bridgeErrors, hypothesis, startInvestigation } from "./helpers.js";

test.afterEach(async ({ page }) => {
  expect(await bridgeErrors(page)).toEqual([]);
});

test("[R9.6] [R9.10] Copy link on a hypothesis gives its canonical URL, and that URL reopens it after reload", async ({ page, context, browserName }) => {
  if (browserName === "chromium") await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await startInvestigation(page, "Cache hit rate fell to 20%.", "Cache hit rate");
  await addHypothesis(page, "Cache keys now include a timestamp");
  await hypothesis(page, "Cache keys now include").getByRole("button", { name: "Copy link" }).click();
  const notice = page.getByRole("status").filter({ hasText: "/h/" }).first();
  await expect(notice).toContainText(/#\/i\/[0-9a-f-]{36}\/h\/[0-9a-f-]{36}$/);
  const url = ((await notice.textContent()) ?? "").match(/https?:\S+$/)?.[0] ?? "";
  if (browserName === "chromium") expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(url);
  await page.goto(url);
  await page.reload();
  await expect(page.getByTestId("focus")).toContainText("Cache keys now include a timestamp");
  await expect(hypothesis(page, "Cache keys now include")).toHaveAttribute("data-focused", "true");
  await page.getByRole("link", { name: "Show the whole investigation" }).click();
  await expect(page.getByTestId("focus")).toHaveCount(0);
});

test("[R9.8] [R9.9] a link with a reserved parameter is refused, and a missing hypothesis offers a way back", async ({ page }) => {
  await startInvestigation(page, "Uploads time out.", "Upload timeouts");
  const workspace = page.url();
  await page.goto(workspace + "/h/1a2b3c4d-5e6f-4a1b-8c2d-3e4f5a6b7c8d");
  await expect(page.getByRole("heading", { level: 1, name: "Not found" })).toBeVisible();
  await page.getByRole("link", { name: "Back to the investigation" }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Upload timeouts" })).toBeVisible();
  await page.goto(workspace + "?token=ghp_abc");
  await expect(page.getByText("carries the parameter \"token\"")).toBeVisible();
});
