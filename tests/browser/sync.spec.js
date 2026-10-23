// AC-09 / CAUSA-PROV-*: publishing working copies to a FAKE GitHub Git Data API (page.route, tests/browser/fake-github.js);
// no request reaches github.com. Only a confirmed publish is ever shown as saved (R10.6, CAUSA-PROV-23).
import { expect, test } from "@playwright/test";
import { createFake } from "./fake-github.js";
import { addHypothesis, bridgeErrors, fill, open, startInvestigation } from "./helpers.js";

const token = "ghp_TESTtokenNeverShown0123456789";
const branch = "causa-workspace";

const configure = async (page) => {
  await open(page, "#/settings");
  await fill(page, "Repository owner", "kevinmmiller");
  await fill(page, "Repository name", "rci-data");
  await fill(page, "Branch", branch);
  await fill(page, "Access token", token);
  await page.getByRole("button", { name: "Save settings" }).click();
  await expect(page.getByText("Publishing working copies to GitHub repository kevinmmiller/rci-data")).toBeVisible();
  return (await page.getByTestId("run-id").textContent()) ?? "";
};

const remote = (page) => page.getByTestId("remote-status");
const idOf = (page) => page.url().split("#/i/")[1];

test.afterEach(async ({ page }) => {
  expect(await bridgeErrors(page)).toEqual([]);
});

test("AC-09 (fake GitHub) a change is saved only after the commit is re-read; offline changes stay pending and publish when back online", async ({ page, context }) => {
  const github = createFake({ token, branch });
  await github.install(page);
  const runId = await configure(page);
  await startInvestigation(page, "Payments webhook retries pile up.", "Webhook retries");
  await expect(remote(page)).toContainText("Saved to GitHub (confirmed by re-reading the commit)");
  await expect(page.getByTestId("local-status")).toHaveText("Saved on this device");
  const path = `workspaces/demo/${runId}/investigations/${idOf(page)}/`;
  const first = github.fileAt(path + "events.ndjson");
  expect(first).toBeDefined();

  github.state.offline = true;
  await context.setOffline(true);
  await expect(page.getByText("Offline — work continues on this device")).toBeVisible();
  await addHypothesis(page, "Webhook signature secret rotated");
  await expect(remote(page)).toContainText("Not yet on GitHub");
  await expect(remote(page)).not.toContainText("Saved to GitHub");
  await expect(page.getByTestId("local-status")).toHaveText("Saved on this device");

  github.state.offline = false;
  await context.setOffline(false);
  await expect(remote(page)).toContainText("Saved to GitHub (confirmed");
  const second = github.fileAt(path + "events.ndjson");
  expect(second.length).toBeGreaterThan(first.length);
  expect(second.subarray(0, first.length).equals(first)).toBe(true);
  expect(github.log.filter((c) => c.method === "PATCH").length).toBe(2);
  // Git Data API only: never the Contents API.
  expect(github.log.filter((c) => c.url.includes("/contents/"))).toEqual([]);

  // The token only travels in the Authorization header: never in URLs, bodies or the page.
  for (const call of github.log) {
    expect(call.url).not.toContain(token);
    expect(call.body).not.toContain(token);
    expect(call.authorization).toBe(`Bearer ${token}`);
  }
  expect(page.url()).not.toContain(token);
  expect(await page.content()).not.toContain(token);
});

test("AC-09 (fake GitHub) another writer's change is a conflict: nothing is overwritten and GitHub's version is used only on request", async ({ page }) => {
  const github = createFake({ token, branch });
  await github.install(page);
  const runId = await configure(page);
  await startInvestigation(page, "Nightly backup is late.", "Late backup");
  await expect(remote(page)).toContainText("Saved to GitHub (confirmed");
  const path = `workspaces/demo/${runId}/investigations/${idOf(page)}/`;
  // Another writer changes the investigation directory on GitHub (adds a file); this device must never overwrite it.
  const theirs = github.fileAt(path + "events.ndjson");
  github.commitFiles("someone else", { [path + "notes.txt"]: "x" });
  const head = github.head();
  await addHypothesis(page, "Backup window overlaps the batch job");
  await expect(remote(page)).toContainText("Conflict — GitHub has a different version");
  await expect(remote(page)).not.toContainText("Saved to GitHub");
  expect(github.head()).toBe(head);
  expect(github.fileAt(path + "events.ndjson").equals(theirs)).toBe(true);
});

test("[R10.6] without GitHub settings the workspace says it is local only", async ({ page }) => {
  await startInvestigation(page, "Report generation is slow.", "Slow reports");
  await expect(remote(page)).toHaveText("Local only — not connected to GitHub");
});

test("[R11.3] (fake GitHub) the token is not saved on the device; after a reload it must be entered again", async ({ page }) => {
  const github = createFake({ token, branch });
  await github.install(page);
  await configure(page);
  const stored = await page.evaluate(() => Object.keys(localStorage).map((k) => localStorage.getItem(k) ?? "").join("\n"));
  expect(stored).not.toContain(token);
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.getByText("No access token in this session")).toBeVisible();
  await startInvestigation(page, "Queue depth grows overnight.", "Queue depth");
  await expect(remote(page)).toContainText("enter your access token");
  await expect(remote(page)).not.toContainText("Saved to GitHub");
});
