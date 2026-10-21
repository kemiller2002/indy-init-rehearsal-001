// Shared steps for the end-to-end journey. Every step drives the real page
// (Limen kernel + F# engine in WebAssembly); nothing here decides legality.
import { expect } from "@playwright/test";

export const open = async (page, hash = "#/") => {
  await page.goto("./" + hash);
  await expect(page.locator("html")).toHaveAttribute("data-kernel", "running");
};

export const bridgeErrors = (page) =>
  page.evaluate(() => (Reflect.get(window, "rciDiagnostics") ?? []).filter((d) => d.kind === "BridgeError"));

/** Fill a labelled field; Playwright's fill + the later blur commit it (change event). */
export const fill = async (page, label, value) => {
  const field = page.getByLabel(label, { exact: true });
  await field.fill(value);
  await field.blur();
};

/** Choose the option of a labelled select whose text contains `text`. */
export const choose = async (page, label, text) => {
  const select = page.getByLabel(label, { exact: true });
  await expect(select.locator("option", { hasText: text }).first()).toBeAttached();
  const value = await select.locator("option", { hasText: text }).first().getAttribute("value");
  await select.selectOption(value ?? "");
};

export const startInvestigation = async (page, observation, title) => {
  await open(page);
  await fill(page, "What did you observe?", observation);
  if (title) await fill(page, "Short title (optional)", title);
  await page.getByRole("button", { name: "Start investigation" }).click();
  await expect(page.getByRole("heading", { level: 1, name: title ?? observation })).toBeVisible();
  await expect(page).toHaveURL(/#\/i\/[0-9a-f-]{36}$/);
};

export const addHypothesis = async (page, statement) => {
  await fill(page, "New hypothesis", statement);
  await page.getByRole("button", { name: "Add hypothesis" }).click();
  await expect(hypothesis(page, statement)).toBeVisible();
};

export const hypothesis = (page, text) => page.getByTestId("hypothesis").filter({ hasText: text });

export const addEvidence = async (page, content, source, { category, url } = {}) => {
  if (url) await page.getByLabel("Evidence is", { exact: true }).selectOption("url");
  await fill(page, "Evidence (text or link)", content);
  if (category) await page.getByLabel("Kind of evidence", { exact: true }).selectOption(category);
  await fill(page, "Where it came from (source)", source);
  await page.getByRole("button", { name: "Add evidence" }).click();
  await expect(page.getByTestId("evidence").filter({ hasText: content })).toBeVisible();
};

export const link = async (page, evidenceText, relationship, hypothesisText) => {
  await choose(page, "Evidence to link", evidenceText);
  await page.getByLabel("Relationship", { exact: true }).selectOption(relationship);
  await choose(page, "Hypothesis to link", hypothesisText);
  await page.getByRole("button", { name: "Link evidence" }).click();
  await expect(page.getByTestId("relationship").filter({ hasText: evidenceText }).filter({ hasText: hypothesisText }).first()).toBeVisible();
};

/** Press an action on a hypothesis after giving the shared reason and evidence. */
export const changeHypothesis = async (page, text, action, reason, evidenceText) => {
  await fill(page, "Reason for the change", reason);
  if (evidenceText) await choose(page, "Evidence that caused it", evidenceText);
  await hypothesis(page, text).getByRole("button", { name: action }).click();
};

export const stateOf = (page, text) => hypothesis(page, text).getByTestId("hypothesis-state");

export const refusal = (page) => page.getByTestId("refusal");

/** Two hypotheses, evidence for each, one supported and one ruled out. */
export const supportedAndFalsified = async (page) => {
  await startInvestigation(page, "Checkout page returns HTTP 502 for about 30% of requests since 02:00 UTC.", "Checkout 502 errors");
  await addHypothesis(page, "Expired TLS certificate on the load balancer");
  await addHypothesis(page, "DNS misconfiguration after the migration");
  await addEvidence(page, "openssl shows the load balancer certificate expired at 01:58 UTC", "openssl s_client on lb-2", { category: "Measurement" });
  await addEvidence(page, "DNS resolves correctly from every region", "dig from three regions", { category: "Measurement" });
  await link(page, "openssl shows", "Supports", "Expired TLS");
  await link(page, "DNS resolves", "Contradicts", "DNS misconfiguration");
  await changeHypothesis(page, "Expired TLS", "Mark plausible", "Timing matches the failures");
  await expect(stateOf(page, "Expired TLS")).toHaveText("Plausible");
  await changeHypothesis(page, "Expired TLS", "Mark supported", "Certificate expiry matches the first 502", "openssl shows");
  await expect(stateOf(page, "Expired TLS")).toHaveText("Supported by evidence");
  await changeHypothesis(page, "DNS misconfiguration", "Rule out (falsify)", "DNS answers are correct everywhere", "DNS resolves");
  await expect(stateOf(page, "DNS misconfiguration")).toHaveText("Ruled out");
};

export const proposeCause = async (page) => {
  await fill(page, "Problem being explained", "Checkout 502 errors");
  await choose(page, "Root cause", "Expired TLS");
  await fill(page, "How it causes the problem (mechanism)", "The load balancer rejects TLS handshakes with the expired certificate, so upstream calls fail with 502.");
  await page.getByRole("button", { name: "Propose cause" }).click();
  await expect(page.getByTestId("cause")).toHaveCount(1);
};
