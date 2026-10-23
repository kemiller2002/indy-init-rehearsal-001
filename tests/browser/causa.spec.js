// CAUSA-REHEARSAL-INTEGRATION-001: the frozen Causa storage release (causa-data@causa-storage-v1.0.0) in the real
// page. The data is the vendored release bytes the build ships; expectations are read from the frozen golden states
// and the release lock, never from the app's own output.
import { readFileSync } from "node:fs";
import { expect, test } from "@playwright/test";
import { addHypothesis, bridgeErrors, fill, open } from "./helpers.js";

const release = "data/causa-storage-v1.0.0";
const lock = JSON.parse(readFileSync(`${release}/compatibility/v1.0.0/lock.json`, "utf8"));
const manifest = JSON.parse(readFileSync(`${release}/fixtures/v1.0.0/manifest.json`, "utf8"));
const golden = (key) => JSON.parse(readFileSync(`${release}/contract/v1.0.0/golden/${key}.state.json`, "utf8"));
const investigations = manifest.investigations.map((i) => ({
  id: i.investigationId,
  key: i.scenarioKey,
  title: i.title,
  state: golden(i.scenarioKey),
  digest: lock.expectedStates.find((s) => s.investigationId === i.investigationId).stateSha256,
}));
const bundleUrl = "**/data/causa-storage-v1.0.0.bundle.json";

/** Add a hypothesis and find it by its card title (other cards may quote it as an alternative). */
const addTitledHypothesis = async (page, statement) => {
  await fill(page, "New hypothesis", statement);
  await page.getByRole("button", { name: "Add hypothesis" }).click();
};
const hypothesisTitled = (page, statement) => page.getByTestId("hypothesis").locator("h3").filter({ hasText: statement });

const consoleErrors = (page) => {
  const errors = [];
  page.on("console", (m) => {
    if (m.type() === "error") errors.push(m.text());
  });
  page.on("pageerror", (e) => errors.push(String(e)));
  return errors;
};

/** Serve the real bundle with one file's bytes changed (the corrupted-stream stimulus). */
const corruptBundle = async (page, path, change) => {
  await page.route(bundleUrl, async (route) => {
    const response = await route.fetch();
    const bundle = await response.json();
    bundle.files[path] = Buffer.from(change(Buffer.from(bundle.files[path], "base64").toString("utf8")), "utf8").toString("base64");
    await route.fulfill({ response, body: JSON.stringify(bundle) });
  });
};

test.afterEach(async ({ page }) => {
  expect(await bridgeErrors(page)).toEqual([]);
});

test("Causa: all five frozen investigations load, verify and are listed with their summaries @ipad", async ({ page }) => {
  const errors = consoleErrors(page);
  await open(page);
  await expect(page.getByRole("link", { name: "Causa Root-Cause Investigation" })).toBeVisible();
  await expect(page).toHaveTitle(/Causa/);
  await expect(page.getByTestId("demo-card")).toHaveCount(5);
  await expect(page.getByTestId("demo-summary")).toContainText("5 investigation(s) from causa-storage-v1.0.0");
  const titles = await page.getByTestId("demo-card").locator("h3").allTextContents();
  expect([...titles].sort()).toEqual(investigations.map((i) => i.title).sort());
  for (const inv of investigations) {
    const card = page.getByTestId("demo-card").filter({ hasText: inv.title });
    await expect(card.getByTestId("demo-hypotheses")).toHaveText(String(inv.state.hypotheses.length));
    await expect(card.getByTestId("demo-evidence")).toHaveText(String(inv.state.evidence.length));
    await expect(card.getByTestId("demo-contradictions")).toHaveText(String(inv.state.contradictions.length));
    await expect(card.getByTestId("demo-problem")).not.toBeEmpty();
    await expect(card.getByTestId("demo-activity")).not.toBeEmpty();
  }
  await expect(page.getByTestId("demo-rejected")).toHaveCount(0);
  expect(errors).toEqual([]);
});

test("Causa: each frozen investigation opens read-only with its replayed state and the pinned state digest @ipad", async ({ page }) => {
  const errors = consoleErrors(page);
  for (const inv of investigations) {
    await open(page, `#/demo/${inv.id}`);
    await expect(page.getByRole("heading", { level: 1, name: inv.title })).toBeVisible();
    await expect(page.getByTestId("read-only-banner")).toContainText("causa-storage-v1.0.0");
    await expect(page.getByTestId("contract")).toContainText(inv.digest.slice(0, 12));
    await expect(page.getByText(`Version ${inv.state.version}`, { exact: true })).toBeVisible();
    await expect(page.getByTestId("ov-hypotheses")).toHaveText(new RegExp(`^${inv.state.hypotheses.length} \\(`));
    await expect(page.getByTestId("ov-evidence")).toHaveText(String(inv.state.evidence.length));
    await expect(page.getByTestId("hypothesis")).toHaveCount(inv.state.hypotheses.length);
    await expect(page.getByTestId("evidence")).toHaveCount(inv.state.evidence.length);
    // Read-only: no form and no state-changing action is offered.
    await expect(page.locator("main form")).toHaveCount(0);
    const actions = await page.locator("main button:visible").allTextContents();
    for (const label of actions) expect(label).toMatch(/^(Copy link.*|Create a working copy|Dismiss)$/);
    await expect(page.getByTestId("outstanding")).toBeVisible();
    // A frozen original is never saved anywhere, so no save status is shown for it.
    await expect(page.getByTestId("local-status")).toHaveCount(0);
    await expect(page.getByTestId("remote-status")).toHaveCount(0);
  }
  expect(errors).toEqual([]);
});

test("Causa: a corrupted event stream is refused with its contract code; the other four still load", async ({ page }) => {
  const target = investigations.find((i) => i.key === "api-latency");
  await corruptBundle(page, `fixtures/v1.0.0/investigations/${target.id}/events.ndjson`, (t) => t.replace('"aggregateVersion":3,', '"aggregateVersion":4,'));
  await open(page);
  await expect(page.getByTestId("demo-card")).toHaveCount(4);
  await expect(page.getByTestId("demo-rejection")).toHaveCount(1);
  await expect(page.getByTestId("demo-rejection")).toContainText("CAUSA.EVENTS_HASH_MISMATCH");
  await expect(page.getByTestId("demo-card").filter({ hasText: target.title })).toHaveCount(0);
  // Its frozen view does not exist: nothing of it is shown partially.
  await open(page, `#/demo/${target.id}`);
  await expect(page.getByRole("heading", { level: 1, name: "Not found" })).toBeVisible();
});

test("Causa: a dataset that is not the pinned release is refused as a whole (incompatible state)", async ({ page }) => {
  await corruptBundle(page, "fixtures/v1.0.0/manifest.json", (t) => t.replace("causa-demo-v1", "causa-demo-v2"));
  await open(page);
  await expect(page.getByTestId("demo-failed")).toContainText("CAUSA.FIXTURE_DRIFT");
  await expect(page.getByTestId("demo-card")).toHaveCount(0);
});

test("Causa: a working copy keeps the frozen history, persists across a reload, and the frozen original never changes @ipad", async ({ page }) => {
  const inv = investigations.find((i) => i.key === "intermittent-outage");
  await open(page, `#/demo/${inv.id}`);
  await page.getByTestId("create-working-copy").click();
  await expect(page).toHaveURL(new RegExp(`#/i/${inv.id}$`));
  await expect(page.getByTestId("origin")).toContainText(`causa-storage-v1.0.0/${inv.key}`);
  await expect(page.getByTestId("local-status")).toHaveText("Saved on this device");
  await addHypothesis(page, "Connection pool exhaustion under retry storms");
  await expect(page.getByTestId("hypothesis")).toHaveCount(inv.state.hypotheses.length + 1);
  await expect(page.getByTestId("local-status")).toHaveText("Saved on this device");
  // The stored copy is a Causa v1 file set whose events start with the frozen bytes.
  const stored = await page.evaluate((id) => localStorage.getItem(`rci:inv:${id}`), inv.id);
  const fileSet = JSON.parse(stored);
  expect(fileSet.format).toBe("causa.fileset/1.0.0");
  const frozenEvents = readFileSync(`${release}/fixtures/v1.0.0/investigations/${inv.id}/events.ndjson`);
  expect(Buffer.from(fileSet.events, "base64").subarray(0, frozenEvents.length).equals(frozenEvents)).toBe(true);
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(page.getByTestId("hypothesis")).toHaveCount(inv.state.hypotheses.length + 1);
  await expect(page.getByTestId("hypothesis").filter({ hasText: "Connection pool exhaustion" })).toBeVisible();
  // The frozen original is unchanged.
  await open(page, `#/demo/${inv.id}`);
  await expect(page.getByTestId("hypothesis")).toHaveCount(inv.state.hypotheses.length);
  await expect(page.getByTestId("contract")).toContainText(inv.digest.slice(0, 12));
});

test("Causa: a second window's stale write is refused without losing its changes", async ({ page, context }) => {
  const inv = investigations.find((i) => i.key === "conflicting-metrics");
  await open(page, `#/demo/${inv.id}`);
  await page.getByTestId("create-working-copy").click();
  await expect(page.getByTestId("local-status")).toHaveText("Saved on this device");
  const other = await context.newPage();
  await open(other, `#/i/${inv.id}`);
  await expect(other.getByTestId("hypothesis")).toHaveCount(inv.state.hypotheses.length);
  // The first window changes the investigation; the second, still on the older version, changes it too.
  await addTitledHypothesis(page, "Dashboard query filters out 5xx from the canary");
  await expect(hypothesisTitled(page, "Dashboard query filters out 5xx from the canary")).toBeVisible();
  await expect(page.getByTestId("local-status")).toHaveText("Saved on this device");
  await addTitledHypothesis(other, "Log sampling drops error lines");
  await expect(other.getByTestId("save-conflict")).toContainText("another window changed this investigation");
  await expect(other.getByTestId("local-status")).toContainText("Not saved on this device");
  // Its change is still there, in memory and as a separate conflict copy; the first window's save is intact.
  await expect(hypothesisTitled(other, "Log sampling drops error lines")).toBeVisible();
  const keys = await other.evaluate(() => Object.keys(localStorage));
  expect(keys.some((k) => k.startsWith(`causa:conflict:${inv.id}`))).toBe(true);
  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
  await expect(hypothesisTitled(page, "Dashboard query filters out 5xx")).toBeVisible();
  await expect(hypothesisTitled(page, "Log sampling drops error lines")).toHaveCount(0);
  expect(await bridgeErrors(other)).toEqual([]);
});

test("Causa: the demonstration list and a frozen investigation are usable with the keyboard alone", async ({ page }) => {
  await open(page);
  const first = page.getByTestId("demo-card").first().locator("h3 a");
  const title = (await first.textContent()) ?? "";
  for (let i = 0; i < 20 && !(await first.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press("Tab");
  await expect(first).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { level: 1, name: title })).toBeVisible();
  const create = page.getByTestId("create-working-copy");
  for (let i = 0; i < 30 && !(await create.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press("Tab");
  await expect(create).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/#\/i\/[0-9a-f-]{36}$/);
  await expect(page.getByLabel("New hypothesis", { exact: true })).toBeVisible();
});

const overflow = (page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

test("Causa: no horizontal scrolling at 200% text, on a phone and on a tablet", async ({ page }) => {
  const inv = investigations.find((i) => i.key === "authentication-outage");
  for (const [width, height, zoom] of [
    [1280, 800, "200%"],
    [390, 844, "100%"],
    [834, 1194, "100%"],
    [1194, 834, "100%"],
  ]) {
    await page.setViewportSize({ width, height });
    for (const hash of ["#/", `#/demo/${inv.id}`]) {
      await open(page, hash);
      await page.evaluate((z) => (document.documentElement.style.fontSize = z), zoom);
      await expect(page.locator("main h1").first()).toBeVisible();
      if (hash === "#/") await expect(page.getByTestId("demo-card")).toHaveCount(5);
      expect(await overflow(page), `${hash} at ${width}x${height}, text ${zoom}`).toBeLessThanOrEqual(0);
    }
  }
});
