#!/usr/bin/env python3
"""Verify real-model channel-constrained categories and supplied service authority."""
import json
import sys
import urllib.request

base = sys.argv[1] if len(sys.argv) > 1 else 'http://127.0.0.1:8099'
sample = 'Salem, 191. Go ahead. 43 Woodmeadow Drive, for residential alarm, several motion alarms. Copy, and then reference that previous call. Can you give me an OSI, just for a small second?'
cases = [
    (sample, 'police', 'security_alarm'),
    ('Engine 1, respond to an activated smoke detector at 12 Main Street.', 'fire', 'alarm'),
    ('Patient having difficulty breathing at 44 Millville Street.', 'fire', 'medical'),
    ('207, traffic stop at 42 Kelly Road.', 'police', 'traffic_stop'),
    # Mentioning a fire unit must not override a supplied police source channel.
    ('Engine 1, respond to an activated smoke detector at 12 Main Street.', 'police', None),
]
for text, service, call_type in cases:
    request = urllib.request.Request(base.rstrip('/')+'/transmission',
        data=json.dumps({'text': text, 'service': service, 'defaultCity': 'Salem', 'defaultState': 'NH'}).encode(),
        headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(request, timeout=150) as response:
        result = json.load(response)
    print(service, text, json.dumps(result), flush=True)
    assert result['service'] == service, result
    if call_type is not None:
        assert result['callType'] == call_type, result
    else:
        assert result['callType'] not in ['alarm', 'fire', 'medical', 'rescue'], result
    if text == sample:
        assert result['location']['houseNumber'] == '43'
        assert result['location']['street'] == 'Woodmeadow Drive'
print('PASS: channel-constrained categories and police residential security alarm')
