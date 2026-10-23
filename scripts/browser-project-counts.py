#!/usr/bin/env python3
"""Per-project Playwright counts from the JSON report; fails when a project executed no test.

A project that selects nothing (for example because its browser could not be installed) must never read as a pass
(CAUSA-REHEARSAL-INTEGRATION-001 review finding T7). Flaky (passed on retry) and skipped tests are counted separately.
"""
import json
import sys
from collections import defaultdict


def walk(suite, counts):
    for spec in suite.get("specs", []):
        for test in spec.get("tests", []):
            project = test.get("projectName", "?")
            status = test.get("status", "?")  # expected | unexpected | flaky | skipped
            counts[project][status] += 1
    for child in suite.get("suites", []):
        walk(child, counts)


def main(path):
    report = json.load(open(path, encoding="utf-8"))
    counts = defaultdict(lambda: defaultdict(int))
    for suite in report.get("suites", []):
        walk(suite, counts)
    projects = [p.get("name") for p in report.get("config", {}).get("projects", [])]
    empty = []
    for name in projects:
        c = counts.get(name, {})
        ran = c.get("expected", 0) + c.get("unexpected", 0) + c.get("flaky", 0)
        print(f"{name}: passed {c.get('expected', 0)}, failed {c.get('unexpected', 0)}, flaky {c.get('flaky', 0)}, skipped {c.get('skipped', 0)}")
        if ran == 0:
            empty.append(name)
    if empty:
        print("::error::These browser projects executed no test: " + ", ".join(empty))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
