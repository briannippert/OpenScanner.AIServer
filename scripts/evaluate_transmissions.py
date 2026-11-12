#!/usr/bin/env python3
"""Evaluate a labeled sample from a support DB, without modifying the DB.

Usage: python3 scripts/evaluate_transmissions.py DATABASE BASE_URL REPORT.json
The report contains transmission text; keep it local to the support investigation.
Labels in tests/transmission_support_cases.json are manually reviewed expectations,
not database ground truth. Geocoding is optional; this evaluates classification.
"""
import json
from pathlib import Path
import sqlite3
import sys
import urllib.error
import urllib.request

database, base, report = sys.argv[1:]
cases = json.loads((Path(__file__).resolve().parents[1] / 'tests/transmission_support_cases.json').read_text())
db = sqlite3.connect(Path(database).resolve().as_uri() + '?mode=ro', uri=True)
results = []
for case in cases:
    row = db.execute('SELECT transcription, alphaTag FROM transmissions WHERE id=?', (case['id'],)).fetchone()
    if row is None:
        raise ValueError('Missing transmission: ' + case['id'])
    text, channel = row
    service = 'fire' if channel == 'Salem Fire' else 'police'
    request = urllib.request.Request(base.rstrip('/') + '/transmission',
        data=json.dumps({'text': text, 'service': service}).encode(),
        headers={'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(request, timeout=150) as response:
            result = json.load(response)
        passed = (result == {}) if case['callType'] is None else (
            result.get('callType') == case['callType'] and result.get('service') == service)
    except urllib.error.HTTPError as error:
        result, passed = {'httpStatus': error.code, 'error': error.read().decode()}, False
    if passed and 'location' in case:
        passed = all(result.get('location', {}).get(k) == v for k, v in case['location'].items())
    results.append(case | {'text': text, 'result': result, 'passed': passed})
    Path(report).write_text(json.dumps(results, indent=2) + '\n')
    print('PASS' if passed else 'FAIL', case['id'], 'expected', case['callType'],
          'actual', result.get('callType'), flush=True)
passed = sum(row['passed'] for row in results)
print(f'{passed}/{len(results)} manually labeled transmissions passed')
for split in ['development', 'holdout']:
    subset = [r for r in results if r.get('split') == split]
    print(split, sum(r['passed'] for r in subset), '/', len(subset))
sys.exit(0 if passed == len(results) else 1)
