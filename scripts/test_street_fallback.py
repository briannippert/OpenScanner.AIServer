#!/usr/bin/env python3
"""Integration checks for conservative street fallback using local providers."""
import http.server
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
scenario = ''
house = '16'
street_calls = 0
fields = ['placeName', 'address', 'houseNumber', 'street', 'unit', 'city', 'state', 'postalCode', 'country', 'crossStreet']

class Backend(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass

    def do_GET(self):
        global street_calls
        parsed = urllib.parse.urlparse(self.path)
        query = urllib.parse.parse_qs(parsed.query)
        if parsed.path == '/streets':
            street_calls += 1
            assert query['osm_tag'] == ['highway']
            assert query['q'][0].endswith(', Salem, NH, USA')
            if scenario == 'offline': self.send_error(503); return
            def road(name, city='Salem', state='New Hampshire', west=-71.2008, east=-71.194):
                return {'properties': {'name': name, 'city': city, 'state': state,
                    'countrycode': 'US', 'type': 'street', 'extent': [west, 42.7725, east, 42.7684]}}
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
        self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers()
        self.wfile.write(json.dumps({'done': True, 'message': {'content': json.dumps(result)}}).encode())

backend = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Backend)
threading.Thread(target=backend.serve_forever, daemon=True).start()
with socket.socket() as probe:
    probe.bind(('127.0.0.1', 0)); port = probe.getsockname()[1]
base = f'http://127.0.0.1:{port}'
upstream = f'http://127.0.0.1:{backend.server_port}'
env = dict(os.environ, Address__OllamaUrl=upstream, Geocoding__Endpoint=upstream+'/geocoder',
    Geocoding__StreetEndpoint=upstream+'/streets', Geocoding__TimeoutSeconds='20',
    Whisper__WhisperServerBinary='/nonexistent/test-whisper', DOTNET_ENVIRONMENT='Production')
with tempfile.TemporaryFile(mode='w+') as log:
    app = subprocess.Popen(['dotnet', 'bin/Debug/net10.0/OpenScanner.WhisperServer.dll', '--urls', base],
        cwd=ROOT, env=env, stdout=log, stderr=log)
    try:
        for _ in range(100):
            try: urllib.request.urlopen(base+'/models'); break
            except urllib.error.URLError: time.sleep(.1)
        else: log.seek(0); raise RuntimeError(log.read())
        def extract():
            req = urllib.request.Request(base+'/address', data=json.dumps({'text': 'test'}).encode(),
                headers={'Content-Type': 'application/json'})
            with urllib.request.urlopen(req, timeout=30) as response: return json.load(response)
        for index, (name, expected) in enumerate([
            ('valid', True), ('wrong-town', False), ('wrong-state', False), ('wrong-type', False),
            ('unrelated', False), ('missing-cross', False), ('far-cross', False), ('ambiguous', False),
            ('wrong-number', False), ('census-wrong-town', False), ('offline', False),
            ('no-cross', True), ('exact', True)]):
            scenario, house = name, str(16+index)
            count = street_calls
            result = extract()
            assert result['address'] == f'{house} Shepherd Ave', result
            assert (result['geocode'] is not None) == expected, (name, result)
            if name == 'exact': assert street_calls == count
            if name == 'valid':
                assert 'Shepard Avenue' in result['geocode']['matchedAddress']
                assert result['geocode']['latitude'] == 42.7713
                assert result['street'] == 'Shepherd Ave' and result['crossStreet'] == 'Ivan Guile Road and Cole Street'
                count = street_calls
                assert extract() == result and street_calls == count
            print('PASS:', name, flush=True)
    finally:
        app.terminate(); app.wait(timeout=10); backend.shutdown(); backend.server_close()
