#!/usr/bin/env python3
"""Real Ollama, Photon and Census regression for a misspelled street plus cross streets."""
import json
import sys
import urllib.request

base = sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:8099'
payload = {
    'text': 'Rescue 3, Engine 3, Medic 1, respond to 16, Shepherd Ave. For an echo level cardiac arrest, cross streets of Ivan Guile Road and Cole Street, timeout 1901.',
    'service': 'fire', 'defaultCity': 'Salem', 'defaultState': 'NH'
}
request = urllib.request.Request(base.rstrip('/')+'/transmission', data=json.dumps(payload).encode(),
    headers={'Content-Type': 'application/json'})
with urllib.request.urlopen(request, timeout=150) as response:
    result = json.load(response)
print(json.dumps(result, indent=2))
assert result['callType'] == 'medical' and result['service'] == 'fire'
location = result['location']
assert location['address'] == '16 Shepherd Ave' and location['street'] == 'Shepherd Ave'
assert location['houseNumber'] == '16' and location['placeName'] is None
match = location['geocode']
assert match and match['matchedAddress'] == '16 SHEPARD AVE, SALEM, NH, 03079'
assert match['city'] == 'Salem' and match['state'] == 'NH'
assert 42.77 < match['latitude'] < 42.78 and -71.20 < match['longitude'] < -71.19
print('PASS: medical classification, request locality, cross-street fallback and preserved source spelling')
