// Real-browser checks of the built site (dist/), as GitHub Pages serves it.
//
// - E2E_BASE_URL set (e.g. https://kevinmmiller.us/indy-init-rehearsal-001/):
//   the same suite runs against that URL and no local server starts.
// - Otherwise dist/ is served by `python3 -m http.server` under the sub-path
//   /indy-init-rehearsal-001/ to mimic Pages. Run `npm run build` first.
//
// Projects: desktop Chromium; Chromium with iPad Pro 11 portrait and landscape
// viewports and touch (this is Chromium EMULATION, not Safari); and WebKit with
// the iPad Pro 11 profile, which runs only when Playwright's WebKit is installed
// (CI installs it; locally it is skipped). Tests always navigate relative to the
// base URL (page.goto("./#/")), never to "/".
import { existsSync, mkdirSync, rmSync, symlinkSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { chromium, defineConfig, devices, webkit } from "@playwright/test";

const subPath = "indy-init-rehearsal-001";
const port = 4321;
const remote = process.env.E2E_BASE_URL;
const baseURL = remote ? (remote.endsWith("/") ? remote : remote + "/") : `http://127.0.0.1:${port}/${subPath}/`;

// Some environments ship a Chromium that Playwright did not download itself.
const preinstalledChromium = "/opt/pw-browsers/chromium";
const chromiumLaunch = existsSync(preinstalledChromium) ? { executablePath: preinstalledChromium } : {};

const installed = (browserType) => {
  try {
    return existsSync(browserType.executablePath());
  } catch {
    return false;
  }
};
const hasWebkit = installed(webkit);
const hasChromium = existsSync(preinstalledChromium) || installed(chromium);

// The served root holds one entry, the sub-path, linked to dist/.
const serveRoot = join(tmpdir(), "rci-pages-root");
if (!remote) {
  rmSync(serveRoot, { recursive: true, force: true });
  mkdirSync(serveRoot, { recursive: true });
  symlinkSync(resolve("dist"), join(serveRoot, subPath), "dir");
}

const ipad = devices["iPad Pro 11"];
const ipadLandscape = devices["iPad Pro 11 landscape"];

export default defineConfig({
  testDir: "./tests/browser",
  fullyParallel: false,
  workers: 1,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  timeout: 90_000,
  expect: { timeout: 15_000 },
  reporter: process.env.CI ? [["github"], ["list"]] : [["list"]],
  use: { baseURL, trace: "retain-on-failure" },
  projects: [
    { name: "chromium-desktop", use: { ...devices["Desktop Chrome"], launchOptions: chromiumLaunch } },
    {
      name: "chromium-ipad-portrait (Chromium emulation, not Safari)",
      use: { ...ipad, browserName: "chromium", defaultBrowserType: "chromium", launchOptions: chromiumLaunch },
      grep: /@ipad/
    },
    {
      name: "chromium-ipad-landscape (Chromium emulation, not Safari)",
      use: { ...ipadLandscape, browserName: "chromium", defaultBrowserType: "chromium", launchOptions: chromiumLaunch },
      grep: /@ipad/
    },
    // WebKit is the closest available stand-in for iPad Safari; it is skipped
    // (no tests selected) when Playwright's WebKit browser is not installed.
    {
      name: "webkit-ipad",
      use: { ...ipad, browserName: "webkit" },
      grep: hasWebkit ? /.*/ : /$^/
    }
  ].filter((project) => project.name.startsWith("webkit") || hasChromium),
  webServer: remote
    ? undefined
    : {
        command: `python3 -m http.server ${port} --bind 127.0.0.1 --directory ${serveRoot}`,
        url: baseURL,
        reuseExistingServer: !process.env.CI,
        stdout: "ignore",
        stderr: "ignore",
        timeout: 60_000
      }
});
