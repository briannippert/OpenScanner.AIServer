#!/usr/bin/env python3
"""Check real model dispatch extraction against a running server (not mock Ollama).

Usage: python3 scripts/test_address_dispatch_live.py [http://127.0.0.1:8099]
Requires the configured Ollama model. Requests also exercise automatic geocoding.
"""
import json
import sys
import urllib.request

base = sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:8099'
cases = [
    ('207, Ill stop. 207. 42, Kelly Road, Mass, 3, Victor, Yankee Yankee, 6-2. Copy.',
     {'address': '42 Kelly Road', 'houseNumber': '42', 'street': 'Kelly Road'}),
    ('315, traffic stop. 315. 18, Main Street, Mass, 7, Alpha, Bravo, 22. Copy.',
     {'address': '18 Main Street', 'houseNumber': '18', 'street': 'Main Street'}),
    ('207, Ill stop. Mass, 3, Victor, Yankee Yankee, 6-2. Copy.', {}),
    ('Respond to Main Street and Kelly Road.',
     {'address': 'Main Street and Kelly Road', 'street': 'Main Street', 'crossStreet': 'Kelly Road'}),
]
fields = ['placeName', 'address', 'houseNumber', 'street', 'unit', 'city',
          'state', 'postalCode', 'country', 'crossStreet']
for text, expected in cases:
    request = urllib.request.Request(base.rstrip('/') + '/address',
        data=json.dumps({'text': text}).encode(), headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(request, timeout=150) as response:
        actual = json.load(response)
    extracted = {field: actual[field] for field in fields}
    assert extracted == dict.fromkeys(fields) | expected, (text, extracted)
    if not expected:
        assert actual['geocode'] is None, actual
    print('PASS:', text, flush=True)
