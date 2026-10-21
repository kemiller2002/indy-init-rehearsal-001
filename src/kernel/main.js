// @ts-check
// Composition root: loads the F# engine (.NET WebAssembly) and hands it to the
// Limen kernel. No application decision is made here; the engine decides,
// the kernel binds and runs effects. Imports resolve through the page's
// relative import map (./vendor/...), so the site works under any sub-path.

import { BrowserKernel } from "limen/kernel/browser-kernel.js";
import { DotnetWasmTransport } from "limen/hosts/dotnet-wasm-transport.js";
import { lifecycleCapability } from "limen/capabilities/lifecycle/index.js";
import "folio/components/register.js";

/** @type {unknown[]} */
const diagnostics = [];
Reflect.set(window, "rciDiagnostics", diagnostics);

const transport = new DotnetWasmTransport({
  // Resolved against this module, so the site works under any sub-path.
  loadRuntime: () => import(new URL("./wasm/_framework/dotnet.js", import.meta.url).href),
  exportPath: ["RciEngine", "Dispatch"],
});

const kernel = new BrowserKernel(
  transport,
  document,
  {
    report: (event) => {
      diagnostics.push(event);
      if (event.kind === "BridgeError") console.error("Limen", event.phase, event.detail);
    },
  },
  { requireHandshake: true, capabilities: [lifecycleCapability()] },
);

await kernel.start();
document.documentElement.dataset.kernel = kernel.status;
if (kernel.status !== "running") {
  // A failed start is reported through diagnostics; tell the person plainly.
  const boot = document.querySelector("[data-boot]");
  if (boot !== null) boot.textContent = "The investigator could not start in this browser. Reload the page to try again.";
}
