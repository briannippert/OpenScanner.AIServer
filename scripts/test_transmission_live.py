#!/usr/bin/env python3
"""Exercise real Ollama classification: python3 scripts/test_transmission_live.py [base URL]."""
import json
import sys
import urllib.request

base = sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:8099'

def extract(text, service=None):
    request = urllib.request.Request(base.rstrip('/') + '/transmission',
        data=json.dumps({'text': text, 'service': service}).encode(),
        headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(request, timeout=150) as response:
        result = json.load(response)
    print(text, json.dumps(result), flush=True)
    return result

for text in ['copy', "I'm out", 'roger']:
    assert extract(text, 'police') == {}

result = extract('207, Ill stop. 207. 42, Kelly Road, Mass, 3, Victor, Yankee Yankee, 6-2. Copy.', 'police')
assert result['callType'] == 'traffic_stop' and result['service'] == 'police'
location = result['location']
assert location['address'] == '42 Kelly Road' and location['houseNumber'] == '42'
assert location['street'] == 'Kelly Road'
assert location['state'] is None and location['placeName'] is None and location['crossStreet'] is None

result = extract('Engine 2 dispatched to 123 Main Street for a structure fire.', 'fire')
assert result['callType'] == 'fire' and result['service'] == 'fire'
assert result['incidentStatus'] == 'dispatched' and result['location']['placeName'] is None
assert result['location']['houseNumber'] == '123'

result = extract('Patient having difficulty breathing.')
assert result['callType'] == 'medical' and result['summary']
assert all(value is None for value in result['location'].values())
print('PASS: real-model chatter, traffic stop, fire dispatch, and medical call extraction')
