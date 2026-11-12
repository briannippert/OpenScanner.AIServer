#!/usr/bin/env python3
"""Real Ollama/Photon regression for an address not covered by Census."""
import json
import sys
import urllib.request

base = sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:8099'
payload = {
    'text': 'Rescue 1, Engine 1, Medic 1, respond to 3 Artisan Drive, apartment number 204, for adult level breathing problems, cross streets of Mall Road and Tuscan Boulevard, timeout 7:44.',
    'service': 'fire', 'defaultCity': 'Salem', 'defaultState': 'NH'
}
request = urllib.request.Request(base.rstrip('/')+'/transmission', data=json.dumps(payload).encode(),
    headers={'Content-Type': 'application/json'})
with urllib.request.urlopen(request, timeout=150) as response:
    result = json.load(response)
print(json.dumps(result, indent=2))
assert result['callType'] == 'medical'
location = result['location']
assert location['houseNumber'] == '3' and location['street'] == 'Artisan Drive'
assert '204' in location['unit']
match = location['geocode']
assert match and match['houseNumber'] == '3' and match['street'] == 'Artisan Drive'
assert match['city'] == 'Salem' and match['state'] in ['NH', 'New Hampshire']
assert match['precision'] == 'building' and match['provider'] == 'Photon / OpenStreetMap'
assert 42.771 < match['latitude'] < 42.772 and -71.230 < match['longitude'] < -71.228
print('PASS: Artisan Drive medical call resolves to a verified OSM building without Census coverage')
