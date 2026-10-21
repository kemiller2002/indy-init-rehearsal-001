// AC-09 persistence truth with a MOCKED GitHub API (page.route): no request
// reaches github.com. Pending is never shown as synced (R10.6).
import { expect, test } from "@playwright/test";
import { addHypothesis, bridgeErrors, fill, open, startInvestigation } from "./helpers.js";

const token = "ghp_TESTtokenNeverShown0123456789";

const cors = {
  "access-control-allow-origin": "*",
  "access-control-allow-headers": "*",
  "access-control-allow-methods": "GET, PUT, OPTIONS",
  "access-control-expose-headers": "*"
};

/** A fake GitHub contents API. `mode.offline` makes it fail like a dropped network. */
const mockGitHub = async (page, mode) => {
  const calls = [];
  await page.route("https://api.github.com/**", async (route) => {
    const request = route.request();
    if (request.method() === "OPTIONS") return route.fulfill({ status: 204, headers: cors });
    calls.push({ method: request.method(), url: request.url(), authorization: request.headers()["authorization"] });
    if (mode.offline) return route.abort("internetdisconnected");
    if (request.method() === "GET") {
      return route.fulfill({ status: 404, headers: { ...cors, "content-type": "application/json" }, body: JSON.stringify({ message: "Not Found" }) });
    }
    if (mode.conflict) {
      return route.fulfill({ status: 409, headers: { ...cors, "content-type": "application/json" }, body: JSON.stringify({ message: "is at abc but expected def" }) });
    }
    mode.writes = (mode.writes ?? 0) + 1;
    return route.fulfill({
      status: 201,
      headers: { ...cors, "content-type": "application/json" },
      body: JSON.stringify({ content: { sha: "sha-" + mode.writes, path: "x" }, commit: { sha: "c-" + mode.writes } })
    });
  });
  return calls;
};

const configure = async (page) => {
  await open(page, "#/settings");
  await fill(page, "Repository owner", "kevinmmiller");
  await fill(page, "Repository name", "rci-data");
  await fill(page, "Access token", token);
  await page.getByRole("button", { name: "Save settings" }).click();
  await expect(page.getByText("Syncing to GitHub repository kevinmmiller/rci-data")).toBeVisible();
};

const remote = (page) => page.getByTestId("remote-status");

test.afterEach(async ({ page }) => {
  expect(await bridgeErrors(page)).toEqual([]);
});

test("AC-09 (GitHub API mocked) offline changes stay pending, never synced, and sync when back online", async ({ page, context }) => {
  const mode = { offline: false };
  const calls = await mockGitHub(page, mode);
  await configure(page);
  await startInvestigation(page, "Payments webhook retries pile up.", "Webhook retries");
  await expect(remote(page)).toContainText("Saved to GitHub (kevinmmiller/rci-data)");
  await expect(page.getByTestId("local-status")).toHaveText("Saved on this device");

  mode.offline = true;
  await context.setOffline(true);
  await expect(page.getByText("Offline — work continues on this device")).toBeVisible();
  await addHypothesis(page, "Webhook signature secret rotated");
  await expect(remote(page)).toContainText("pending");
  await expect(remote(page)).not.toContainText("Saved to GitHub");
  await expect(page.getByTestId("local-status")).toHaveText("Saved on this device");

  mode.offline = false;
  await context.setOffline(false);
  await expect(remote(page)).toContainText("Saved to GitHub");
  expect(calls.filter((c) => c.method === "PUT").length).toBeGreaterThanOrEqual(2);

  // The token only travels in the Authorization header: never in URLs or the page.
  for (const call of calls) {
    expect(call.url).not.toContain(token);
    expect(call.authorization).toBe(`Bearer ${token}`);
  }
  expect(page.url()).not.toContain(token);
  expect(await page.content()).not.toContain(token);
});

test("AC-09 (GitHub API mocked) a 409 from GitHub is shown as a conflict, not as saved", async ({ page }) => {
  const mode = { offline: false, conflict: true };
  await mockGitHub(page, mode);
  await configure(page);
  await startInvestigation(page, "Nightly backup is late.", "Late backup");
  await expect(remote(page)).toContainText("Conflict with GitHub");
  await expect(remote(page)).toContainText("Nothing was overwritten");
  await expect(remote(page)).not.toContainText("Saved to GitHub");
  await expect(page.getByRole("button", { name: "Check GitHub again" })).toBeVisible();
});

test("[R10.6] without GitHub settings the workspace says it is local only", async ({ page }) => {
  await startInvestigation(page, "Report generation is slow.", "Slow reports");
  await expect(remote(page)).toHaveText("Local only — not synced to GitHub");
});
