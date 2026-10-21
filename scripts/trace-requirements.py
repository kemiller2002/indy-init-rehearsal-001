"""Requirement traceability: map test results (xUnit TRX + Playwright JSON) to scoped requirements.

usage: trace.py INVENTORY.json OUT.json [--trx FILE]... [--pw FILE]... [--commit SHA] [--source local|ci]
A requirement is 'tested' when >=1 test cites it and every citing test passed in the given results;
'failing' if any citing test failed; 'untested' when no test cites it. Whether that counts as
Verified is decided by the caller from --source (only CI results on the release commit count).
"""
import json, re, sys, xml.etree.ElementTree as ET
from collections import defaultdict

TAG = re.compile(r'\[(R\d+\.\d+|A\d+|AC-\d+|D-\d+)\]|\b(AC-\d+|R\d+\.\d+)\b')

# Frozen acceptance plan (baseline/acceptance-plan.md): which requirements each AC proves.
AC_REQUIREMENTS = {
    'AC-01': ['R1.1', 'R1.2', 'R1.3'], 'AC-02': ['R3.1', 'R3.2'],
    'AC-03': ['R2.1', 'R2.2', 'R2.3', 'R2.4', 'R16.4'], 'AC-04': ['R3.3', 'R3.4'],
    'AC-05': ['R6.3'], 'AC-06': ['R8.1'], 'AC-07': ['R6.1', 'R8.2', 'R8.3', 'R16.3'],
    'AC-08': ['R10.5', 'R10.7'], 'AC-09': ['R10.5', 'R10.6'], 'AC-10': ['R12.1', 'R12.2', 'R12.3'],
    'AC-11': ['R7.3', 'R7.4'], 'AC-14': ['R9.3'],
}

def tags_of(name):
    found = {a or b for a, b in TAG.findall(name)}
    return found | {r for t in found for r in AC_REQUIREMENTS.get(t, [])}

def args(argv):
    opts = defaultdict(list); it = iter(argv)
    for a in it:
        if a.startswith('--'): opts[a[2:]].append(next(it))
        else: opts['_'].append(a)
    return opts

def trx_results(path):
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    root = ET.parse(path).getroot()
    return [(r.get('testName'), r.get('outcome') == 'Passed') for r in root.iterfind('.//t:UnitTestResult', ns)]

def pw_results(path):
    def walk(suite, prefix):
        here = prefix + ([suite['title']] if suite.get('title') else [])
        for spec in suite.get('specs', []):
            for test in spec.get('tests', []):
                statuses = [r.get('status') for r in test.get('results', [])]
                if test.get('status') == 'skipped' or statuses == ['skipped']: continue
                yield (' '.join(here + [spec['title'], f"({test.get('projectName')})"]), test.get('status') == 'expected')
        for s in suite.get('suites', []): yield from walk(s, here)
    return [r for s in json.load(open(path)).get('suites', []) for r in walk(s, [])]

def main(argv):
    o = args(argv); inv_path, out_path = o['_'][:2]
    inv = json.load(open(inv_path))
    results = [r for p in o['trx'] for r in trx_results(p)] + [r for p in o['pw'] for r in pw_results(p)]
    cites = defaultdict(list)
    for name, ok in results:
        for tag in tags_of(name): cites[tag].append({'test': name, 'passed': ok})
    scoped = [r for r in inv['requirements'] if r['scope'] in ('scoped', 'scoped-factory', 'process')]
    def status(sid):
        ts = cites.get(sid, [])
        return 'untested' if not ts else ('failing' if not all(t['passed'] for t in ts) else 'tested')
    rows = [{'id': r['id'], 'sourceId': r['sourceId'], 'title': r['title'], 'scope': r['scope'],
             'status': status(r['sourceId']), 'tests': cites.get(r['sourceId'], [])} for r in scoped]
    acc = sorted({t for t in cites if t.startswith(('AC-', 'A', 'D-'))})
    summary = defaultdict(int)
    for r in rows: summary[r['status']] += 1
    out = {'schema': 'echelon.experiment.traceability/v1', 'commit': (o['commit'] or [None])[0], 'source': (o['source'] or ['local'])[0],
           'totals': {'tests': len(results), 'passed': sum(ok for _, ok in results)}, 'summary': dict(summary),
           'requirements': rows, 'otherTags': {t: cites[t] for t in acc}}
    json.dump(out, open(out_path, 'w'), indent=2)
    print(json.dumps({'summary': out['summary'], 'totals': out['totals']}))

if __name__ == '__main__': main(sys.argv[1:])
