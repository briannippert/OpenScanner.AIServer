#!/usr/bin/env python3
"""Integration checks for conservative street fallback using local providers."""
import http.server
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
scenario = ''
house = '16'
requested_city = 'Salem'
street_calls = 0
fields = ['placeName', 'address', 'houseNumber', 'street', 'unit', 'city', 'state', 'postalCode', 'country', 'crossStreet']

class Backend(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass

    def do_GET(self):
        global street_calls
        parsed = urllib.parse.urlparse(self.path)
        query = urllib.parse.parse_qs(parsed.query)
        if parsed.path == '/buildings':
            if scenario == 'building-outage': self.send_error(503); return
            features = []
            if scenario.startswith('building-'):
                feature = {'properties': {'type': 'house', 'osm_key': 'building', 'housenumber': house,
                    'street': 'Shepherd Avenue', 'city': 'Salem', 'state': 'New Hampshire',
                    'countrycode': 'US', 'postcode': '03079'},
                    'geometry': {'type': 'Point', 'coordinates': [-71.1987, 42.7713]}}
                prop = feature['properties']
                if scenario == 'building-wrong-number': prop['housenumber'] = '999'
                if scenario == 'building-wrong-street': prop['street'] = 'Main Street'
                if scenario == 'building-wrong-town': prop['city'] = 'Concord'
                if scenario == 'building-wrong-state': prop['state'] = 'Massachusetts'
                if scenario == 'building-street-centroid': prop['type'] = 'street'
                if scenario == 'building-invalid-coordinates': feature['geometry']['coordinates'][1] = 100
                features = [feature]
                if scenario == 'building-ambiguous':
                    features += [feature | {'geometry': {'type': 'Point', 'coordinates': [-71.1988, 42.7714]}}]
                if scenario == 'building-duplicate': features += [feature]
            payload = {'features': features}
        elif parsed.path == '/streets':
            street_calls += 1
            assert query['osm_tag'] == ['highway']
            assert query['q'][0].endswith(', '+requested_city+', NH, USA')
            if scenario == 'offline': self.send_error(503); return
            def road(name, city=None, state='New Hampshire', west=-71.2008, east=-71.194):
                return {'properties': {'name': name, 'city': city or requested_city, 'state': state,
                    'countrycode': 'US', 'type': 'street', 'extent': [west, 42.7725, east, 42.7684]},
                    'geometry': {'type': 'Point', 'coordinates': [(west+east)/2, 42.77045]}}
            text = query['q'][0].split(',')[0]
            if text.startswith('Ivan'):
                # Only the final short-name query finds the misspelled cross street.
                features = [road('Ivan Gile Road')] if text == 'Ivan' else []
            elif text.startswith('Cole'):
                features = [] if scenario == 'missing-cross' else [road('Cole Street')]
                if scenario == 'far-cross': features = [road('Cole Street', west=-71.5, east=-71.49)]
            else:
                features = [] if text == 'Shepherd Ave' else [road('Shepard Avenue')]
                if scenario == 'wrong-town': features = [road('Shepard Avenue', city='Concord')]
                if scenario == 'wrong-state': features = [road('Shepard Avenue', state='Massachusetts')]
                if scenario == 'wrong-type': features = [road('Shepard Road')]
                if scenario == 'unrelated': features = [road('Sheridan Avenue')]
                if scenario == 'ambiguous': features += [road('Sheperd Avenue')]
            if scenario.startswith('building-') and scenario != 'building-outage': features = []
            if scenario.startswith('center-'):
                features = [road('Shepherd Avenue')]
                if scenario == 'center-segments': features += [road('Shepherd Avenue', west=-71.194, east=-71.188)]
                if scenario == 'center-wrong-town': features = [road('Shepherd Avenue', city='Concord')]
                if scenario == 'center-wrong-state': features = [road('Shepherd Avenue', state='Massachusetts')]
                if scenario == 'center-fuzzy': features = [road('Shepard Avenue')]
                if scenario == 'center-no-point': features[0].pop('geometry')
                if scenario == 'center-invalid-point': features[0]['geometry']['coordinates'][1] = 100
            payload = {'features': features}
        elif parsed.path == '/geocoder':
            query_address = query['address'][0]
            matches = []
            if 'Shepherd' not in query_address or scenario == 'exact':
                canonical = query_address.split(',')[0]
                if scenario == 'wrong-number': canonical = '999 Shepard Avenue'
                suffix = 'CONCORD, NH' if scenario == 'census-wrong-town' else 'SALEM, NH'
                matches = [{'matchedAddress': f'{canonical}, {suffix}, 03079',
                            'coordinates': {'x': -71.1987, 'y': 42.7713}}]
            payload = {'result': {'addressMatches': matches}}
        else:
            raise AssertionError(self.path)
        self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers()
        self.wfile.write(json.dumps(payload).encode())

    def do_POST(self):
        if self.headers.get('Transfer-Encoding') == 'chunked':
            raw = b''
            while True:
                size = int(self.rfile.readline().strip(), 16)
                if not size: self.rfile.readline(); break
                raw += self.rfile.read(size); self.rfile.read(2)
        else:
            raw = self.rfile.read(int(self.headers['Content-Length']))
        request = json.loads(raw)
        assert self.path == '/api/chat'
        result = dict.fromkeys(fields)
        cross = None if scenario == 'no-cross' else 'Ivan Guile Road and Cole Street'
        result.update(address=f'{house} Shepherd Ave', houseNumber=house, street='Shepherd Ave', crossStreet=cross)
        if scenario.startswith('center-'):
            result.update(address=None, houseNumber=None, crossStreet='Lake')
        self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers()
        self.wfile.write(json.dumps({'done': True, 'message': {'content': json.dumps(result)}}).encode())

backend = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Backend)
threading.Thread(target=backend.serve_forever, daemon=True).start()
with socket.socket() as probe:
    probe.bind(('127.0.0.1', 0)); port = probe.getsockname()[1]
base = f'http://127.0.0.1:{port}'
upstream = f'http://127.0.0.1:{backend.server_port}'
env = dict(os.environ, Address__OllamaUrl=upstream, Geocoding__Endpoint=upstream+'/geocoder',
    Geocoding__PlaceEndpoint=upstream+'/buildings',
    Geocoding__StreetEndpoint=upstream+'/streets', Geocoding__TimeoutSeconds='20',
    Geocoding__Gazetteer__Enabled='false',
    Whisper__WhisperServerBinary='/nonexistent/test-whisper', DOTNET_ENVIRONMENT='Production')
with tempfile.TemporaryFile(mode='w+') as log:
    app = subprocess.Popen(['dotnet', 'bin/Debug/net10.0/OpenScanner.AIServer.dll', '--urls', base],
        cwd=ROOT, env=env, stdout=log, stderr=log)
    try:
        for _ in range(100):
            try: urllib.request.urlopen(base+'/models'); break
            except urllib.error.URLError: time.sleep(.1)
        else: log.seek(0); raise RuntimeError(log.read())
        def extract():
            req = urllib.request.Request(base+'/address', data=json.dumps({'text': 'test', 'defaultCity': requested_city}).encode(),
                headers={'Content-Type': 'application/json'})
            with urllib.request.urlopen(req, timeout=30) as response: return json.load(response)
        for index, (name, expected) in enumerate([
            ('valid', True), ('wrong-town', False), ('wrong-state', False), ('wrong-type', False),
            ('unrelated', False), ('missing-cross', False), ('far-cross', False), ('ambiguous', False),
            ('wrong-number', False), ('census-wrong-town', False), ('offline', False),
            ('no-cross', True), ('exact', True), ('building-valid', True), ('building-duplicate', True),
            ('building-ambiguous', False), ('building-wrong-number', False), ('building-wrong-street', False),
            ('building-wrong-town', False), ('building-wrong-state', False), ('building-street-centroid', False),
            ('building-invalid-coordinates', False), ('building-outage', True),
            ('center-valid', True), ('center-segments', True), ('center-wrong-town', False),
            ('center-wrong-state', False), ('center-fuzzy', False), ('center-no-point', False),
            ('center-invalid-point', False)]):
            if '--centers-only' in sys.argv and not name.startswith('center-'): continue
            scenario, house = name, str(16+index)
            requested_city = 'Test '+name if name.startswith('center-') else 'Salem'
            count = street_calls
            result = extract()
            assert result['address'] == (None if name.startswith('center-') else f'{house} Shepherd Ave'), result
            assert (result['geocode'] is not None) == expected, (name, result)
            if name == 'exact': assert street_calls == count
            if name in ('building-valid', 'building-duplicate', 'building-ambiguous'):
                assert street_calls == count
            if name in ('building-valid', 'building-duplicate'):
                assert result['geocode']['precision'] == 'building'
                assert result['geocode']['provider'] == 'Photon / OpenStreetMap'
                assert result['geocode']['houseNumber'] == house
            if name in ('center-valid', 'center-segments'):
                assert result['geocode']['precision'] == 'street'
                assert result['geocode']['houseNumber'] is None
                assert result['geocode']['street'] == 'Shepherd Avenue'
                assert result['crossStreet'] == 'Lake' and result['houseNumber'] is None
                count = street_calls
                assert extract() == result and street_calls == count
            if name == 'valid':
                assert 'Shepard Avenue' in result['geocode']['matchedAddress']
                assert result['geocode']['latitude'] == 42.7713
                assert result['street'] == 'Shepherd Ave' and result['crossStreet'] == 'Ivan Guile Road and Cole Street'
                count = street_calls
                assert extract() == result and street_calls == count
            print('PASS:', name, flush=True)
    finally:
        app.terminate(); app.wait(timeout=10); backend.shutdown(); backend.server_close()
