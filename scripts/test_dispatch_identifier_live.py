#!/usr/bin/env python3
"""Check real model dispatch identifiers and location isolation against a running server."""
import json
import sys
import urllib.request

base = sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:8099'

def extract(text):
    request = urllib.request.Request(base.rstrip('/')+'/transmission',
        data=json.dumps({'text': text, 'service': 'fire', 'defaultCity': 'Salem', 'defaultState': 'NH'}).encode(),
        headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(request, timeout=150) as response:
        result = json.load(response)
    print(text, json.dumps(result), flush=True)
    return result

for text, call_type in [
    ('Fire alarm to all companies responding. 16 Shepherd Ave. Be advised, message sent to your MD table.', 'other'),
    ('Fire alarm to Engine 2 responding to 42 Kelly Road, updated notes are on your mobile terminal.', 'other'),
    ('Fire alarm to Rescue 3, respond to 44 Millville Street for breathing problems.', 'medical'),
    ('Fire alarm to Engine 1, activated smoke detector at 12 Main Street.', 'alarm'),
    ('Engine 1, respond to a fire alarm at 12 Main Street.', 'alarm'),
    ('Fire alarm answering Engine 2. Reported structure fire at 123 Main Street.', 'fire'),
]:
    result = extract(text)
    assert result['callType'] == call_type, result
    assert result['location']['placeName'] is None, result
assert extract('Fire alarm, Engine 1. Go ahead. Received.') == {}
result = extract("Engine 1. Engine 1, it's a 74-year-old female. She's located inside Market Basket. She's feeling faint. She has a history of fib. Engine 1, edit it, thank you.")
assert result['callType'] == 'medical' and result['location']['placeName'] == 'Market Basket'
result = extract('Engine 1, respond for a welfare check; the caller requests help opening a door, no medical complaint reported.')
assert result['callType'] != 'welfare_check'


result = extract('Rescue 2, medic 1, 44 Millville Street, building 1, apartment 1, for breathing problems.')
assert result['callType'] == 'medical'
location = result['location']
assert location['address'].startswith('44 Millville Street') and location['houseNumber'] == '44'
assert location['street'] == 'Millville Street' and location['placeName'] is None
assert location['geocode'] is None or location['geocode']['matchedAddress'].startswith('44 MILLVILLE ST,')
print('PASS: dispatch identifiers, real alarms/fire/medical, and independent numbered addresses')
