// @ts-check
// Live GitHub persistence (CAUSA-REHEARSAL-INTEGRATION-001, Phase 5): the real app, in a real browser, publishing to
// the real api.github.com with a short-lived token. Only run by .github/workflows/causa-live-persistence.yml.
// Traces, screenshots and videos are OFF: the token is typed into the page and must never be recorded.
import { existsSync, mkdirSync, rmSync, symlinkSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { defineConfig, devices } from "@playwright/test";

const subPath = "indy-init-rehearsal-001";
const port = Number(process.env.E2E_PORT ?? 4388);
const baseURL = `http://127.0.0.1:${port}/${subPath}/`;
const preinstalledChromium = "/opt/pw-browsers/chromium";
const serveRoot = join(tmpdir(), "rci-live-root");
rmSync(serveRoot, { recursive: true, force: true });
mkdirSync(serveRoot, { recursive: true });
symlinkSync(resolve("dist"), join(serveRoot, subPath), "dir");

export default defineConfig({
  testDir: "./tests/live",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 300_000,
  expect: { timeout: 60_000 },
  reporter: [["list"]],
  use: { baseURL, trace: "off", screenshot: "off", video: "off" },
  projects: [
    { name: "chromium-live", use: { ...devices["Desktop Chrome"], launchOptions: existsSync(preinstalledChromium) ? { executablePath: preinstalledChromium } : {} } }
  ],
  webServer: {
    command: `python3 -m http.server ${port} --bind 127.0.0.1 --directory ${serveRoot}`,
    url: baseURL,
    reuseExistingServer: false,
    stdout: "ignore",
    stderr: "ignore",
    timeout: 60_000
  }
});
