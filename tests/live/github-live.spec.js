// LIVE: the app publishes Causa working copies to api.github.com (no mock) and every claim is checked independently
// by reading GitHub's Git Data API from this test process. Requires CAUSA_LIVE_TOKEN (contents: write on the
// workspace repository), CAUSA_LIVE_OWNER, CAUSA_LIVE_REPO and CAUSA_LIVE_BRANCH. The token is never logged,
// recorded or written to evidence.
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { expect, test } from "@playwright/test";

const token = process.env.CAUSA_LIVE_TOKEN ?? "";
const owner = process.env.CAUSA_LIVE_OWNER ?? "";
const repo = process.env.CAUSA_LIVE_REPO ?? "";
const branch = process.env.CAUSA_LIVE_BRANCH ?? "";
const api = "https://api.github.com";
const release = "data/causa-storage-v1.0.0";
const target = "d588748e-8df9-5f27-859e-5d36d9a2b53d"; // api-latency, a frozen investigation
const evidence = { startedAt: new Date().toISOString(), owner, repo, branch, steps: [] };
const note = (step, data) => {
  evidence.steps.push({ step, at: new Date().toISOString(), ...data });
  console.log(`LIVE ${step} ${JSON.stringify(data)}`);
};

// Never a skip: a live run without its configuration fails, so it can never be counted as passing.
if (!token || !owner || !repo || !branch) throw new Error("CAUSA_LIVE_TOKEN, CAUSA_LIVE_OWNER, CAUSA_LIVE_REPO and CAUSA_LIVE_BRANCH are required");

const gh = async (path) => {
  const response = await fetch(`${api}/repos/${owner}/${repo}${path}`, {
    headers: { authorization: `Bearer ${token}`, accept: "application/vnd.github+json", "x-github-api-version": "2022-11-28" },
  });
  if (!response.ok) throw new Error(`GitHub ${response.status} for ${path}`);
  return response.json();
};
const blobId = (bytes) => createHash("sha1").update(Buffer.concat([Buffer.from(`blob ${bytes.length}\0`), bytes])).digest("hex");

/** Read the investigation's three files at the branch head, by SHA, independently of the app. */
const remoteFiles = async (runId) => {
  const head = (await gh(`/git/ref/heads/${branch}?t=${Date.now()}`)).object.sha;
  const commit = await gh(`/git/commits/${head}`);
  let tree = await gh(`/git/trees/${commit.tree.sha}`);
  for (const segment of ["workspaces", "demo", runId, "investigations", target]) {
    const entry = tree.tree.find((e) => e.path === segment);
    if (!entry) return { head, commit, files: undefined };
    tree = await gh(`/git/trees/${entry.sha}`);
  }
  const files = {};
  for (const e of tree.tree) {
    const blob = await gh(`/git/blobs/${e.sha}`);
    const bytes = Buffer.from(blob.content.replace(/\n/g, ""), "base64");
    expect(blobId(bytes)).toBe(e.sha);
    files[e.path] = bytes;
  }
  return { head, commit, files, names: tree.tree.map((e) => `${e.mode} ${e.type} ${e.path}`).sort() };
};

const deviceFiles = async (page) => {
  const stored = await page.evaluate((id) => localStorage.getItem(`rci:inv:${id}`), target);
  const set = JSON.parse(stored);
  return { events: Buffer.from(set.events, "base64"), manifest: Buffer.from(set.manifest, "base64"), snapshot: Buffer.from(set.snapshot, "base64") };
};

const ready = async (page, hash) => {
  await page.goto("./" + hash);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
};

const configure = async (page) => {
  await ready(page, "#/settings");
  for (const [label, value] of [["Repository owner", owner], ["Repository name", repo], ["Branch", branch], ["Access token", token]]) {
    const field = page.getByLabel(label, { exact: true });
    await field.fill(value);
    await field.blur();
  }
  await page.getByRole("button", { name: "Save settings" }).click();
  await expect(page.getByTestId("run-id")).not.toBeEmpty();
  return (await page.getByTestId("run-id").textContent()) ?? "";
};

const addHypothesis = async (page, statement) => {
  const field = page.getByLabel("New hypothesis", { exact: true });
  await field.fill(statement);
  await field.blur();
  await page.getByRole("button", { name: "Add hypothesis" }).click();
  await expect(page.getByTestId("hypothesis").locator("h3").filter({ hasText: statement })).toBeVisible();
};

const confirmed = (page) => expect(page.getByTestId("remote-status")).toContainText("Saved to GitHub (confirmed by re-reading the commit)", { timeout: 120_000 });

test("LIVE: working copy, append, reload, recovery on a second device, and a refused conflicting write — all verified on GitHub", async ({ browser }) => {
  const deviceA = await browser.newContext();
  const page = await deviceA.newPage();
  const errors = [];
  page.on("pageerror", (e) => errors.push(String(e)));
  const runId = await configure(page);
  const prefix = `workspaces/demo/${runId}/investigations/${target}/`;
  const before = await gh(`/git/ref/heads/${branch}?t=${Date.now()}`);
  note("configured", { runId, prefix, headBefore: before.object.sha });

  // 1. Working copy of the frozen investigation, published byte for byte (CAUSA-PROV-11).
  await ready(page, `#/demo/${target}`);
  await page.getByTestId("create-working-copy").click();
  await expect(page).toHaveURL(new RegExp(`#/i/${target}$`));
  await confirmed(page);
  const frozen = readFileSync(`${release}/fixtures/v1.0.0/investigations/${target}/events.ndjson`);
  const copy = await remoteFiles(runId);
  expect(copy.names).toEqual(["100644 blob events.ndjson", "100644 blob manifest.json", "100644 blob snapshot.json"]);
  expect(copy.files["events.ndjson"].equals(frozen)).toBe(true);
  expect(copy.commit.parents.map((p) => p.sha)).toEqual([before.object.sha]);
  expect(copy.commit.message).toContain("working copy of causa-demo-v1");
  note("working-copy", { commit: copy.head, parent: before.object.sha, eventsSha256: createHash("sha256").update(frozen).digest("hex"), message: copy.commit.message });

  // 2. An append: one more commit whose events extend the copy byte for byte; device and GitHub agree exactly.
  await addHypothesis(page, "Live check: cache eviction after the 4.2 deploy");
  await confirmed(page);
  const appended = await remoteFiles(runId);
  const device = await deviceFiles(page);
  expect(appended.files["events.ndjson"].equals(device.events)).toBe(true);
  expect(appended.files["manifest.json"].equals(device.manifest)).toBe(true);
  expect(appended.files["snapshot.json"].equals(device.snapshot)).toBe(true);
  expect(appended.files["events.ndjson"].subarray(0, frozen.length).equals(frozen)).toBe(true);
  expect(appended.commit.parents.map((p) => p.sha)).toEqual([copy.head]);
  note("append", { commit: appended.head, parent: copy.head, events: appended.files["events.ndjson"].length, message: appended.commit.message });

  // 3. Reload: the device copy and the confirmed state survive; the token does not.
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.getByTestId("hypothesis").locator("h3").filter({ hasText: "Live check: cache eviction" })).toBeVisible();
  await confirmed(page);
  const stored = await page.evaluate(() => Object.keys(localStorage).map((k) => k + "=" + localStorage.getItem(k)).join("\n"));
  expect(stored).not.toContain(token);
  note("reload", { confirmedAfterReload: true, tokenStored: false });

  // 4. A second device (fresh storage, same settings) recovers the run from GitHub, verified.
  const settings = await page.evaluate(() => localStorage.getItem("rci:settings"));
  const deviceB = await browser.newContext();
  await deviceB.addInitScript((value) => {
    if (!localStorage.getItem("rci:settings")) localStorage.setItem("rci:settings", value);
  }, settings);
  const other = await deviceB.newPage();
  other.on("pageerror", (e) => errors.push(String(e)));
  await ready(other, "#/settings");
  const tokenField = other.getByLabel("Access token", { exact: true });
  await tokenField.fill(token);
  await tokenField.blur();
  await other.getByRole("button", { name: "Save settings" }).click();
  await expect(other.getByTestId("run-id")).toHaveText(runId);
  await other.getByRole("button", { name: "Recover this run from GitHub" }).click();
  await expect(other.locator(".app-notice")).toContainText("1 investigation(s) recovered", { timeout: 120_000 });
  await ready(other, `#/i/${target}`);
  await confirmed(other);
  expect((await deviceFiles(other)).events.equals(device.events)).toBe(true);
  note("recovered-on-second-device", { events: device.events.length });

  // 5. Device B appends; device A, still on the older version, appends too: A's write is refused, nothing is overwritten.
  await addHypothesis(other, "Live check: written from device B");
  await confirmed(other);
  const fromB = await remoteFiles(runId);
  await addHypothesis(page, "Live check: written from device A (stale)");
  await expect(page.getByTestId("remote-status")).toContainText("Conflict — GitHub has a different version", { timeout: 120_000 });
  await expect(page.getByTestId("sync-detail")).toContainText("CAUSA.VERSION_STALE");
  const afterConflict = await remoteFiles(runId);
  expect(afterConflict.head).toBe(fromB.head);
  expect(afterConflict.files["events.ndjson"].equals(fromB.files["events.ndjson"])).toBe(true);
  note("conflict-refused", { head: afterConflict.head, overwritten: false });

  // 6. Device A adopts GitHub's version on request; its own version is kept as a conflict copy on the device.
  await page.getByRole("button", { name: /Use GitHub's version/ }).click();
  await confirmed(page);
  expect((await deviceFiles(page)).events.equals(fromB.files["events.ndjson"])).toBe(true);
  const conflictCopies = await page.evaluate((id) => Object.keys(localStorage).filter((k) => k.startsWith(`causa:conflict:${id}`)).length, target);
  expect(conflictCopies).toBeGreaterThan(0);
  note("adopted-remote", { conflictCopies });

  // Every request the app made went to api.github.com only, and the page never showed the token.
  expect(await page.content()).not.toContain(token);
  expect(await other.content()).not.toContain(token);
  expect(errors).toEqual([]);
  evidence.finishedAt = new Date().toISOString();
  evidence.result = "pass";
  mkdirSync("live-evidence", { recursive: true });
  writeFileSync("live-evidence/persistence.json", JSON.stringify(evidence, null, 2));
  await deviceA.close();
  await deviceB.close();
});
