// Builds the deterministic fixture once, by driving the control arm's UI.
// Output: fixture/snapshot.json (the rci:* localStorage entries) + its sha256.
// usage: node fixture.mjs <baseUrl> <outFile>
import { createHash } from "node:crypto";
import { writeFileSync } from "node:fs";
import { existsSync } from "node:fs";
import { chromium, expect } from "@playwright/test";

const [base, out] = process.argv.slice(2);
const preinstalled = "/opt/pw-browsers/chromium";
const browser = await chromium.launch(existsSync(preinstalled) ? { executablePath: preinstalled } : {});
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
await page.clock.setFixedTime(new Date("2026-10-22T09:00:00Z"));

const open = async (hash) => {
  await page.goto(base + hash);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
};
const fill = async (label, value) => {
  const f = page.getByLabel(label, { exact: true });
  await f.fill(value);
  await f.blur();
};
const choose = async (label, text) => {
  const s = page.getByLabel(label, { exact: true });
  const v = await s.locator("option", { hasText: text }).first().getAttribute("value");
  await s.selectOption(v ?? "");
};
const click = (name) => page.getByRole("button", { name }).click();
const hyp = (t) => page.getByTestId("hypothesis").filter({ hasText: t });

await open("#/");
await fill("What did you observe?", "Checkout page returns HTTP 502 for about 30% of requests since 02:00 UTC.");
await fill("Short title (optional)", "Checkout 502 errors");
await click("Start investigation");
await expect(page).toHaveURL(/#\/i\/[0-9a-f-]{36}$/);

await fill("New hypothesis", "Expired TLS certificate on the load balancer");
await click("Add hypothesis");
await fill("New hypothesis", "DNS misconfiguration after the migration");
await click("Add hypothesis");
const evidence = async (content, source, category) => {
  await fill("Evidence (text or link)", content);
  await page.getByLabel("Kind of evidence", { exact: true }).selectOption(category);
  await fill("Where it came from (source)", source);
  await click("Add evidence");
  await expect(page.getByTestId("evidence").filter({ hasText: content })).toBeVisible();
};
await evidence("openssl shows the load balancer certificate expired at 01:58 UTC", "openssl s_client on lb-2", "Measurement");
await evidence("DNS resolves correctly from every region", "dig from three regions", "Measurement");
const link = async (ev, rel, h) => {
  await choose("Evidence to link", ev);
  await page.getByLabel("Relationship", { exact: true }).selectOption(rel);
  await choose("Hypothesis to link", h);
  await click("Link evidence");
};
await link("openssl shows", "Supports", "Expired TLS");
await link("DNS resolves", "Contradicts", "DNS misconfiguration");
const change = async (h, action, reason, ev) => {
  await fill("Reason for the change", reason);
  if (ev) await choose("Evidence that caused it", ev);
  await hyp(h).getByRole("button", { name: action }).click();
};
await change("Expired TLS", "Mark plausible", "Timing matches the failures");
await change("Expired TLS", "Mark supported", "Certificate expiry matches the first 502", "openssl shows");
await change("DNS misconfiguration", "Rule out (falsify)", "DNS answers are correct everywhere", "DNS resolves");
await expect(hyp("DNS misconfiguration").getByTestId("hypothesis-state")).toHaveText("Ruled out");

await fill("Problem being explained", "Checkout 502 errors");
await choose("Root cause", "Expired TLS");
await fill("How it causes the problem (mechanism)", "The load balancer rejects TLS handshakes with the expired certificate, so upstream calls fail with 502.");
await click("Propose cause");
await click("Confirm root cause");
await expect(page.getByTestId("cause")).toContainText("Confirmed");

await page.getByLabel("Kind of fix", { exact: true }).selectOption("Correction");
await fill("What will be done", "Renew the certificate and add expiry monitoring");
await choose("Cause it addresses", "C1");
await click("Add fix");
const fix = page.getByTestId("correction");
await fix.getByRole("button", { name: "Approve" }).click();
await fix.getByRole("button", { name: "Start work" }).click();
await fix.getByRole("button", { name: "Mark implemented" }).click();
await choose("Fix to check", "A1");
await fill("Success looks like", "No 502 responses for 24 hours");
await click("Add success criterion");
await fix.getByRole("button", { name: "Start verification" }).click();
await expect(page.getByTestId("resolve-blocker").filter({ hasText: "RESOLVE.CORRECTION_NOT_VERIFIED" })).toBeVisible();

const snapshot = await page.evaluate(() =>
  Object.fromEntries(Object.keys(localStorage).filter((k) => k.startsWith("rci")).sort().map((k) => [k, localStorage.getItem(k)])));
const investigation = await page.evaluate(() => location.hash.split("/")[2]);
const body = JSON.stringify({ investigation, clock: "2026-10-22T09:00:00Z", storage: snapshot }, null, 2) + "\n";
writeFileSync(out, body);
console.log(JSON.stringify({ out, investigation, keys: Object.keys(snapshot), sha256: createHash("sha256").update(body).digest("hex") }));
await browser.close();
