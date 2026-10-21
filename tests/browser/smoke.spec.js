// Smoke: the built site loads every resource under the Pages sub-path, the
// Limen kernel negotiates with the F# engine, and the home view renders.
import { expect, test } from "@playwright/test";

test("the site loads every resource and the engine renders the home view @ipad", async ({ page }) => {
  const failures = [];
  page.on("response", (response) => {
    if (response.status() >= 400) failures.push(`${response.status()} ${response.url()}`);
  });
  page.on("requestfailed", (request) => failures.push(`failed ${request.url()}`));
  page.on("pageerror", (error) => failures.push(`script error: ${error.message}`));

  await page.goto("./#/");
  await expect(page.getByRole("heading", { level: 1, name: "Investigations" })).toBeVisible();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  const diagnostics = await page.evaluate(() => Reflect.get(window, "rciDiagnostics").filter((d) => d.kind === "BridgeError"));
  expect(diagnostics).toEqual([]);
  expect(failures).toEqual([]);
  await expect(page.getByText(/^Build [0-9a-f]{7}|Build unknown/)).toBeVisible();
});

test("the workspace fits a 375px phone screen without sideways scrolling", async ({ page }) => {
  await page.setViewportSize({ width: 375, height: 812 });
  await page.goto("./#/");
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await page.getByLabel("What did you observe?", { exact: true }).fill("A very long observation without spaces: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
  await page.getByRole("button", { name: "Start investigation" }).click();
  await expect(page.getByTestId("observation")).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
});

test("build-info.json names the commit", async ({ request, baseURL }) => {
  const response = await request.get(new URL("build-info.json", baseURL).href);
  expect(response.ok()).toBe(true);
  const info = await response.json();
  expect(typeof info.commit).toBe("string");
  expect(typeof info.builtAt).toBe("string");
});
