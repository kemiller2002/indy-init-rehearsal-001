// Verifies the vendored Causa storage release (data/causa-storage-v1.0.0/, src/contract/*.fs) byte for byte
// against the release lock and VENDOR.json before anything is built or shipped. Read-only; exits 1 on any drift.
// CAUSA-REHEARSAL-INTEGRATION-001; Indy-init requirements/causa/data/v1.0.0 (CAUSA-INV-13, CAUSA-CLAR-12).
import { createHash } from "node:crypto";
import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { dirname, join, relative } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const release = join(root, "data", "causa-storage-v1.0.0");
const sha256 = (path) => createHash("sha256").update(readFileSync(path)).digest("hex");
const walk = (dir) => readdirSync(dir).flatMap((name) => (statSync(join(dir, name)).isDirectory() ? walk(join(dir, name)) : [join(dir, name)]));

const vendor = JSON.parse(readFileSync(join(release, "VENDOR.json"), "utf8"));
const lockPath = join(release, "compatibility", "v1.0.0", "lock.json");
const lock = JSON.parse(readFileSync(lockPath, "utf8"));
const pinned = new Map(lock.frozenFiles.map((f) => [f.path, f.sha256]));

const problems = [
  ...(sha256(lockPath) === vendor.releaseIdentitySha256 ? [] : [`lock.json identity ${sha256(lockPath)} != ${vendor.releaseIdentitySha256}`]),
  ...(lock.releaseTag === vendor.tag ? [] : [`lock releaseTag ${lock.releaseTag} != ${vendor.tag}`]),
  ...(lock.schemaBundleSha256 === vendor.schemaBundleSha256 ? [] : ["schema bundle digest differs from VENDOR.json"]),
  ...(sha256(join(release, "fixtures", "v1.0.0", "manifest.json")) === vendor.datasetIdentitySha256 ? [] : ["dataset identity differs from VENDOR.json"]),
  // every vendored data file must be a frozen file with the locked digest (the lock itself is anchored above)
  ...walk(release)
    .map((path) => relative(release, path).split("\\").join("/"))
    .filter((rel) => rel !== "VENDOR.json" && rel !== "compatibility/v1.0.0/lock.json")
    .flatMap((rel) => (!pinned.has(rel) ? [`unpinned file ${rel}`] : sha256(join(release, rel)) === pinned.get(rel) ? [] : [`digest mismatch ${rel}`])),
  // every frozen fixture, schema and golden file the app loads must be present
  ...[...pinned.keys()]
    .filter((rel) => /^(fixtures\/v1\.0\.0\/|contract\/v1\.0\.0\/(schemas|golden)\/)/.test(rel))
    .flatMap((rel) => (existsSync(join(release, rel)) ? [] : [`missing frozen file ${rel}`])),
  ...vendor.contractLibrary.flatMap((f) => (sha256(join(root, f.path)) === f.sha256 ? [] : [`contract library drift ${f.path}`])),
];

if (problems.length > 0) {
  for (const p of problems) console.error(`FAIL causa-vendor ${p}`);
  process.exit(1);
}
console.log(`PASS causa-vendor ${vendor.tag} (${pinned.size} locked files, ${vendor.contractLibrary.length} library files) release ${vendor.releaseIdentitySha256.slice(0, 12)}`);
