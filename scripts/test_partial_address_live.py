#!/usr/bin/env python3
"""Real model/geocoder regression for notification destinations with an omitted street suffix."""
import json
import sys
import urllib.request

base = sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:8099'
for text in [
    '184. In the area, 16 Garabedian for a crash notification.',
    '220, heading to 16 Garabedian for an accident notification.',
]:
    request = urllib.request.Request(base.rstrip('/')+'/transmission',
        data=json.dumps({'text': text, 'service': 'police', 'defaultCity': 'Salem', 'defaultState': 'NH'}).encode(),
        headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(request, timeout=150) as response:
        result = json.load(response)
    print(text, json.dumps(result), flush=True)
    assert result['callType'] == 'other' and result['service'] == 'police'
    assert 'notification' in result['summary'].lower() and 'traffic stop' not in result['summary'].lower()
    location = result['location']
    assert location['address'] == '16 Garabedian' and location['houseNumber'] == '16'
    assert location['street'] == 'Garabedian' and location['placeName'] is None
    assert location['geocode'] and location['geocode']['matchedAddress'] == '16 GARABEDIAN DR, SALEM, NH, 03079'
    assert location['geocode']['precision'] == 'address'
print('PASS: crash notifications preserve partial destinations and resolve provider-supplied street suffixes')
