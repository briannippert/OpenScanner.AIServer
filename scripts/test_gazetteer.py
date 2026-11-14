#!/usr/bin/env python3
"""Integration checks for the per-city street and business gazetteer, using local providers."""
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
FIELDS = ['placeName', 'address', 'houseNumber', 'street', 'unit', 'city', 'state', 'postalCode', 'country', 'crossStreet']
calls = {'streets': 0, 'buildings': 0, 'select': 0}
overpass_calls = {}
select_answer = 0

def way(way_id, name, nodes):
    return {'type': 'way', 'id': way_id, 'tags': {'highway': 'residential', 'name': name},
            'nodes': [n for n, _, _ in nodes], 'geometry': [{'lat': lat, 'lon': lon} for _, lat, lon in nodes]}

def place(node_id, name, lat, lon, **tags):
    return {'type': 'node', 'id': node_id, 'lat': lat, 'lon': lon, 'tags': {'name': name, 'shop': 'yes', **tags}}

SALEM = [
    way(1, 'Shepard Avenue', [(1, 42.7700, -71.2000), (2, 42.7705, -71.1990), (3, 42.7710, -71.1980)]),
    way(2, 'Cole Street', [(3, 42.7710, -71.1980), (4, 42.7720, -71.1980)]),
    way(3, 'Main Street', [(10, 42.7800, -71.2100), (11, 42.7800, -71.2090), (12, 42.7800, -71.2080)]),
    way(4, 'North Broadway', [(12, 42.7800, -71.2080), (13, 42.7810, -71.2080)]),
    way(5, 'Pelham Road', [(20, 42.7600, -71.2200), (21, 42.7600, -71.2190)]),
    way(6, 'Garabedian Drive', [(30, 42.7500, -71.2300), (31, 42.7500, -71.2290), (32, 42.7500, -71.2280)]),
    # Two streets that meet twice, a kilometre apart: no single corner.
    way(7, 'Loop Road', [(40, 42.7400, -71.2400), (42, 42.7450, -71.2390), (41, 42.7500, -71.2400)]),
    way(8, 'Far Street', [(40, 42.7400, -71.2400), (41, 42.7500, -71.2400)]),
    way(9, 'Kelly Road', [(50, 42.7300, -71.2500), (51, 42.7300, -71.2490)]),
    way(10, 'Kelby Road', [(52, 42.7350, -71.2500), (53, 42.7350, -71.2490)]),
    way(12, 'South Broadway', [(12, 42.7800, -71.2080), (14, 42.7790, -71.2080)]),
    way(13, 'Woodmeadow Drive', [(60, 42.7200, -71.2600), (61, 42.7200, -71.2590)]),
    # A footpath-style way without geometry must be ignored rather than break the load.
    {'type': 'way', 'id': 11, 'tags': {'highway': 'residential', 'name': 'Broken Way'}},
    place(100, 'Market Basket', 42.7801, -71.2095, **{'addr:housenumber': '1', 'addr:street': 'Main Street', 'addr:postcode': '03079'}),
    place(101, "Dunkin'", 42.7701, -71.1999),
    place(102, "Dunkin'", 42.7811, -71.2081),
    {'type': 'way', 'id': 103, 'center': {'lat': 42.7601, 'lon': -71.2195},
     'tags': {'name': 'Salem Fire Department', 'amenity': 'fire_station'}},
]
CENSUS_STREETS = {'Shepard Avenue', 'Pelham Road', 'Kelly Road', 'Kelby Road', 'Photon Street', 'Woodmeadow Drive'}

# What the extraction model "hears" for each transmission.
EXTRACTIONS = {
    'repair': dict(address='16 Shepherd Ave', houseNumber='16', street='Shepherd Ave'),
    'phonetic': dict(address='22 Pelum Road', houseNumber='22', street='Pelum Road'),
    'split': dict(address='43 Wood Meadow Drive', houseNumber='43', street='Wood Meadow Drive'),
    'ambiguous': dict(address='30 Kely Road', houseNumber='30', street='Kely Road'),
    'suffix': dict(address='Garabedian', street='Garabedian'),
    'corner': dict(address='Main and Broadway', street='Main', crossStreet='Broadway'),
    'twice': dict(address='Loop Road and Far Street', street='Loop Road', crossStreet='Far Street'),
    'Market Basket': dict(placeName='Market Basket'),
    'Dunkin': dict(placeName='Dunkin'),
    'Market Baskett': dict(placeName='Market Baskett'),
    'fallback': dict(address='40 Photon St', houseNumber='40', street='Photon St'),
}

class Backend(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass

    def reply(self, payload):
        self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers()
        self.wfile.write(json.dumps(payload).encode())

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        query = urllib.parse.parse_qs(parsed.query)
        if parsed.path == '/buildings':
            calls['buildings'] += 1
            self.reply({'features': []})
        elif parsed.path == '/streets':
            calls['streets'] += 1
            city = query['q'][0].split(', ')[1]
            self.reply({'features': [{'properties': {'name': 'Photon Street', 'city': city, 'state': 'New Hampshire',
                'countrycode': 'US', 'type': 'street', 'extent': [-71.2008, 42.7725, -71.194, 42.7684]},
                'geometry': {'type': 'Point', 'coordinates': [-71.1974, 42.77045]}}]})
        elif parsed.path == '/geocoder':
            first, city = [p.strip() for p in query['address'][0].split(',')][:2]
            street = first.split(' ', 1)[1]
            matches = [{'matchedAddress': f'{first}, {city.upper()}, NH, 03079', 'coordinates': {'x': -71.1987, 'y': 42.7713}}] \
                if street in CENSUS_STREETS else []
            self.reply({'result': {'addressMatches': matches}})
        else:
            raise AssertionError(self.path)

    def do_POST(self):
        if self.headers.get('Transfer-Encoding') == 'chunked':
            raw = b''
            while True:
                size = int(self.rfile.readline().strip(), 16)
                if not size: self.rfile.readline(); break
                raw += self.rfile.read(size); self.rfile.read(2)
        else:
            raw = self.rfile.read(int(self.headers['Content-Length']))
        if self.path == '/overpass':
            query = urllib.parse.parse_qs(raw.decode())['data'][0]
            assert '"ISO3166-2"="US-NH"' in query and 'out geom;' in query, query
            city = next(c for c in ('Salem', 'Nowhere', 'Broken') if f'"name"~"^{c}$",i' in query)
            overpass_calls[city] = overpass_calls.get(city, 0) + 1
            if city == 'Broken': self.send_error(504); return
            self.reply({'elements': SALEM if city == 'Salem' else []})
            return
        assert self.path == '/api/chat'
        request = json.loads(raw)
        if 'selectedMatchIndex' in request['format']['properties']:
            calls['select'] += 1
            content = {'selectedMatchIndex': select_answer}
        else:
            content = dict.fromkeys(FIELDS) | EXTRACTIONS[request['messages'][1]['content']]
        self.reply({'done': True, 'message': {'content': json.dumps(content)}})

backend = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Backend)
threading.Thread(target=backend.serve_forever, daemon=True).start()
with socket.socket() as probe:
    probe.bind(('127.0.0.1', 0)); port = probe.getsockname()[1]
base = f'http://127.0.0.1:{port}'
upstream = f'http://127.0.0.1:{backend.server_port}'

def get(path):
    with urllib.request.urlopen(base + path, timeout=30) as response: return json.load(response)

def address(text, city=None):
    body = {'text': text} | ({'defaultCity': city} if city else {})
    req = urllib.request.Request(base + '/address', data=json.dumps(body).encode(), headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=60) as response: return json.load(response)

def wait_for(condition, what):
    for _ in range(300):
        if condition(): return
        time.sleep(.1)
    raise RuntimeError('Timed out waiting for ' + what)

with tempfile.TemporaryDirectory() as cache, tempfile.TemporaryFile(mode='w+') as log:
    env = dict(os.environ, Address__OllamaUrl=upstream, Geocoding__Endpoint=upstream + '/geocoder',
        Geocoding__PlaceEndpoint=upstream + '/buildings', Geocoding__StreetEndpoint=upstream + '/streets',
        Geocoding__TimeoutSeconds='20', Geocoding__DefaultCity='Salem', Geocoding__DefaultState='NH',
        Geocoding__Gazetteer__Enabled='true', Geocoding__Gazetteer__Endpoint=upstream + '/overpass',
        Geocoding__Gazetteer__CacheDir=cache,
        Whisper__WhisperServerBinary='/nonexistent/test-whisper', DOTNET_ENVIRONMENT='Production')

    def start():
        app = subprocess.Popen(['dotnet', 'bin/Debug/net10.0/OpenScanner.AIServer.dll', '--urls', base],
            cwd=ROOT, env=env, stdout=log, stderr=log)
        for _ in range(100):
            try: urllib.request.urlopen(base + '/models'); return app
            except urllib.error.URLError: time.sleep(.1)
        app.kill(); log.seek(0); raise RuntimeError(log.read())

    app = start()
    try:
        # The default city is downloaded at startup, without any request asking for it.
        wait_for(lambda: get('/health')['gazetteer'], 'the default city to load')
        loaded = get('/health')['gazetteer']
        assert [(c['city'], c['state'], c['streets'], c['places']) for c in loaded] == [('Salem', 'NH', 12, 4)], loaded
        assert overpass_calls == {'Salem': 1}
        saved = json.loads((Path(cache) / 'nh-salem.json').read_text())
        assert len(saved['Streets']) == 12 and saved['Version'] == 1

        # A misheard street is repaired from the list and the house number verified by Census.
        result = address('repair')
        assert result['geocode']['street'] == 'Shepard Avenue' and result['geocode']['houseNumber'] == '16', result
        assert result['street'] == 'Shepherd Ave', result
        # Spelled too differently for edit distance alone, but pronounced the same.
        result = address('phonetic')
        assert result['geocode']['street'] == 'Pelham Road', result
        # One word heard as two.
        result = address('split')
        assert result['geocode']['street'] == 'Woodmeadow Drive', result
        # Two streets fit equally well: no guess.
        assert address('ambiguous')['geocode'] is None

        # A street named without its road type gets the pin of the only street it can be.
        result = address('suffix')
        assert result['geocode']['precision'] == 'street' and result['geocode']['street'] == 'Garabedian Drive', result
        assert result['geocode']['provider'] == 'OpenStreetMap gazetteer', result
        assert (result['geocode']['latitude'], result['geocode']['longitude']) == (42.75, -71.229), result

        # Two streets: the corner where they meet. "Broadway" is both North and South
        # Broadway here, which is still one corner because they meet Main Street together.
        result = address('corner')
        assert result['geocode']['precision'] == 'intersection', result
        assert result['geocode']['matchedAddress'] == 'Main Street & North Broadway, Salem, NH', result
        assert (result['geocode']['latitude'], result['geocode']['longitude']) == (42.78, -71.208), result
        assert result['street'] == 'Main' and result['crossStreet'] == 'Broadway', result
        # Streets meeting in two distant places fall back to the street itself.
        result = address('twice')
        assert result['geocode']['precision'] == 'street' and result['geocode']['street'] == 'Loop Road', result

        # An exact, unique business name needs neither Photon nor the model.
        result = address('Market Basket')
        assert result['geocode']['name'] == 'Market Basket' and result['geocode']['precision'] == 'place', result
        assert result['address'] == '1 Main Street, Salem, NH, 03079' and result['geocode']['latitude'] == 42.7801, result
        assert calls['select'] == 0
        # Two branches: the model chooses using the transmission.
        select_answer = 1
        result = address('Dunkin')
        assert result['geocode']['latitude'] == 42.7811 and calls['select'] == 1, result
        # A close name is offered to the model rather than accepted outright.
        select_answer = None
        assert address('Market Baskett')['geocode'] is None and calls['select'] == 2
        assert calls['streets'] == 0, calls

        # Cities OpenStreetMap cannot supply, or that fail to download, keep using live search.
        for city in ('Nowhere', 'Broken'):
            before = calls['streets']
            result = address('fallback', city)
            assert result['geocode']['street'] == 'Photon Street' and calls['streets'] > before, result
            wait_for(lambda: overpass_calls.get(city) == 1, 'the download attempt for ' + city)
            address('fallback', city)
            time.sleep(1.5)
            assert overpass_calls[city] == 1, overpass_calls
        assert [c['city'] for c in get('/health')['gazetteer']] == ['Salem']
    except BaseException:
        log.seek(0); print(log.read()[-4000:])
        raise
    finally:
        app.terminate(); app.wait(timeout=20)

    # After a restart the city comes from disk, not from another download.
    app = start()
    try:
        wait_for(lambda: get('/health')['gazetteer'], 'the saved city to load')
        assert address('corner')['geocode']['precision'] == 'intersection'
        assert overpass_calls['Salem'] == 1, overpass_calls
    finally:
        app.terminate(); app.wait(timeout=20)
print('ok')
