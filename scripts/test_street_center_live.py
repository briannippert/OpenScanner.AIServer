#!/usr/bin/env python3
"""Verify an approximate street pin for an unnumbered police transmission."""
import json
import sys
import urllib.request

base = sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:8099'
payload = {
    'text': '7-30, just called in a passerby, reported to him that there was an elderly male walking in the roadway carrying a chair on Bluff Street heading west. If you could check Bluff from Lake. 7-10.',
    'service': 'police', 'defaultCity': 'Salem', 'defaultState': 'NH'
}
request = urllib.request.Request(base.rstrip('/')+'/transmission', data=json.dumps(payload).encode(),
    headers={'Content-Type': 'application/json'})
with urllib.request.urlopen(request, timeout=150) as response:
    result = json.load(response)
print(json.dumps(result, indent=2))
location = result['location']
assert location['houseNumber'] is None and location['street'] == 'Bluff Street'
match = location['geocode']
assert match and match['precision'] == 'street' and match['houseNumber'] is None
assert match['street'] == 'Bluff Street' and match['city'] == 'Salem' and match['state'] == 'NH'
assert match['provider'] == 'Photon / OpenStreetMap'
assert 42.79 < match['latitude'] < 42.82 and -71.23 < match['longitude'] < -71.18
print('PASS: Bluff Street resolves to an explicitly approximate street point')
