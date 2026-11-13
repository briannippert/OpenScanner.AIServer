#!/usr/bin/env python3
"""Real-provider regression for a bounded street-name correction plus missing suffix."""
import json
import sys
import urllib.request

base = sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:8099'
payload = {
    'text': 'Salem, 2-28. Go ahead. Copulina, well-being check, 8, Graymoor Woods, apartment 302.',
    'service': 'police', 'defaultCity': 'Salem', 'defaultState': 'NH'
}
request = urllib.request.Request(base.rstrip('/')+'/transmission', data=json.dumps(payload).encode(),
    headers={'Content-Type': 'application/json'})
with urllib.request.urlopen(request, timeout=150) as response:
    result = json.load(response)
print(json.dumps(result, indent=2))
assert result['callType'] == 'welfare_check' and result['service'] == 'police'
location = result['location']
assert location['houseNumber'] == '8' and location['street'] == 'Graymoor Woods'
assert '302' in location['unit']
assert location['geocode'] and location['geocode']['matchedAddress'] == '8 BRAEMOOR WOODS RD, SALEM, NH, 03079'
assert location['geocode']['precision'] == 'address'
print('PASS: Graymoor Woods resolves to provider-verified Braemoor Woods Rd with source text/unit preserved')
