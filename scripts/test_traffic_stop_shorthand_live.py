#!/usr/bin/env python3
"""Real-model regressions for police location/registration shorthand."""
import json
import sys
import urllib.request

base = sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:8099'
cases = [
    ('28 Taco Bell, California, 677-51-John-4.', 'traffic_stop'),
    ('191, Burger King, Massachusetts, 123-Adam-Bravo-4.', 'traffic_stop'),
    ('Residential alarm at 43 Woodmeadow Drive, several motion alarms.', 'security_alarm'),
    ('Stolen vehicle at Taco Bell, California plate 677-51-John-4.', 'theft'),
]
for text, expected in cases:
    request = urllib.request.Request(base.rstrip('/') + '/transmission',
        data=json.dumps({'text': text, 'service': 'police',
                         'defaultCity': 'Salem', 'defaultState': 'NH'}).encode(),
        headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(request, timeout=180) as response:
        result = json.load(response)
    print(text, json.dumps(result), flush=True)
    assert result['callType'] == expected and result['service'] == 'police', result
    if expected == 'traffic_stop':
        location = result['location']
        assert location['placeName'] == ('Taco Bell' if 'Taco Bell' in text else 'Burger King'), result
        assert location['state'] in (None, 'NH', 'New Hampshire'), result
        assert location['houseNumber'] not in ('28', '191'), result
        assert location['street'] not in ('Taco Bell', 'Burger King', 'Route 28'), result
        assert 'alarm' not in result['summary'].lower() and 'california' not in result['summary'].lower(), result
print('PASS: terse traffic stops, plate states, and explicit incident precedence')
