// usage: node steps-json.mjs <stepsTsv> <out.json> — converts timed.sh rows to steps.json
import { readFileSync, writeFileSync } from "node:fs";
const [tsv, out] = process.argv.slice(2);
const rows = readFileSync(tsv, "utf8").split("\n").filter(Boolean).map((l) => l.split("\t"));
const steps = Object.fromEntries(rows.map(([n, rc]) => [n, { exit: Number(rc) }]));
const seconds = Object.fromEntries(rows.map(([n, , s]) => [n, Math.round(Number(s) * 10) / 10]));
writeFileSync(out, JSON.stringify({ ...steps, seconds }, null, 2));
console.log(JSON.stringify({ ...steps, seconds }));
