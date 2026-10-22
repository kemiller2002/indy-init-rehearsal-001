// Serves an arm's dist/ under /indy-init-rehearsal-001/ (as GitHub Pages does).
// usage: node serve.mjs <distDir> <port>
import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import { extname, join, normalize } from "node:path";

const [dist, port = "4400"] = process.argv.slice(2);
const prefix = "/indy-init-rehearsal-001/";
const types = {
  ".html": "text/html; charset=utf-8", ".js": "text/javascript", ".mjs": "text/javascript",
  ".css": "text/css", ".json": "application/json", ".wasm": "application/wasm",
  ".svg": "image/svg+xml", ".png": "image/png", ".dat": "application/octet-stream",
  ".woff2": "font/woff2", ".ico": "image/x-icon", ".blat": "application/octet-stream"
};

const resolvePath = (url) => {
  const path = decodeURIComponent(new URL(url, "http://x").pathname);
  if (!path.startsWith(prefix)) return null;
  const rel = normalize(path.slice(prefix.length)).replace(/^(\.\.[/\\])+/, "");
  return join(dist, rel === "." || rel === "" ? "index.html" : rel);
};

createServer(async (req, res) => {
  const file = resolvePath(req.url ?? "/");
  const target = file && (await stat(file).catch(() => null))?.isDirectory() ? join(file, "index.html") : file;
  const body = target ? await readFile(target).catch(() => null) : null;
  if (!body) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { "content-type": types[extname(target)] ?? "application/octet-stream", "cache-control": "no-store" });
  res.end(body);
}).listen(Number(port), "127.0.0.1", () => console.log(`serving ${dist} at http://127.0.0.1:${port}${prefix}`));
