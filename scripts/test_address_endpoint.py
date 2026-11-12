#!/usr/bin/env python3
"""Exercise /address against mock Ollama; run dotnet build first."""
import http.server, threading, subprocess, os, time, urllib.request, urllib.error, json
import socket
import tempfile
from urllib.parse import urlparse, parse_qs
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
fields = ['placeName', 'address','houseNumber','street','unit','city','state','postalCode','country','crossStreet']
geocode_calls = []
place_calls = []
class Backend(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        query = parse_qs(urlparse(self.path).query)
        if urlparse(self.path).path == '/places':
            query_text = query['q'][0]
            assert query_text.endswith(', Salem, NH, USA')
            place_calls.append(query_text)
            if query_text.startswith('offline'): self.send_error(503); return
            features = []
            for city, state in [('Concord', 'New Hampshire'), ('Salem', 'Massachusetts'), ('Salem', 'New Hampshire')]:
                features.append({'type': 'Feature', 'properties': {'name': 'Coffee Place', 'city': city,
                    'state': state, 'countrycode': 'US', 'street': 'Main Street', 'housenumber': '10', 'postcode': '03079'},
                    'geometry': {'type': 'Point', 'coordinates': [-71.2, 42.8]}})
            if query_text.startswith('missing'): features = []
            if query_text.startswith('wrong-town'): features = features[:2]
            self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers()
            self.wfile.write(json.dumps({'features': features}).encode())
            return
        assert urlparse(self.path).path == '/geocoder'
        assert query['benchmark'] == ['Public_AR_Current'] and query['format'] == ['json']
        address = query['address'][0]
        geocode_calls.append((address, time.monotonic()))
        address = address.removesuffix(', Salem, NH')
        if address == 'unavailable': self.send_error(503); return
        if address == 'timeout': time.sleep(3); return
        matches = [] if address == 'missing' else [
            {'matchedAddress': '123 MAIN ST, NORTH CITY, VA', 'coordinates': {'x': -77.1, 'y': 38.9}}]
        if address == 'ambiguous':
            matches.append({'matchedAddress': '123 MAIN ST, SOUTH CITY, VA', 'coordinates': {'x': -77.2, 'y': 38.8}})
        if address.startswith('294 N Broadway'):
            matches[0]['matchedAddress'] = '294 N BROADWAY, SALEM, NH, 03079'
        if address == 'invalid': del matches[0]['coordinates']['x']
        if address == 'out-of-range': matches[0]['coordinates']['y'] = 100
        self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers()
        self.wfile.write(json.dumps({'result': {'addressMatches': matches}}).encode())
    def log_message(self, *args): pass
    def do_POST(self):
        if self.headers.get('Transfer-Encoding') == 'chunked':
            raw=b''
            while True:
                size=int(self.rfile.readline().strip(),16)
                if not size:
                    self.rfile.readline(); break
                raw+=self.rfile.read(size); self.rfile.read(2)
        else: raw=self.rfile.read(int(self.headers['Content-Length']))
        data=json.loads(raw)
        assert self.path == '/api/chat' and data['think'] is False and data['stream'] is False
        assert data['options']['num_ctx']==4096
        ranking = data['format']['required'] == ['selectedMatchIndex']
        if not ranking: assert data['format']['required']==fields
        text=data['messages'][-1]['content']
        if ranking:
            selection = json.loads(text)
            context = selection['context']
            if context == 'offline': self.send_error(503); return
            if context == 'slow': time.sleep(3); return
            if selection['address'].startswith('Coffee Place'):
                assert len(selection['candidates']) == 1
                assert 'New Hampshire' in selection['candidates'][0]['address']
            selected = 1 if context in ('South City', 'auto ambiguous South City') else (99 if context == 'bad-index' else (0 if context == 'business Coffee Place' else None))
            self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers()
            self.wfile.write(json.dumps({'done': True, 'message': {'content': json.dumps({'selectedMatchIndex': selected})}}).encode())
            return
        if text=='unavailable': self.send_error(503); return
        if text=='timeout': time.sleep(2); return
        result=dict.fromkeys(fields)
        if text=='123 Main St': result.update(address=text, houseNumber='123', street='Main St')
        if text.startswith('auto '):
            query = text.split()[1]
            result.update(address=query, houseNumber='123', street='Main St')
        if text.startswith('business '): result.update(placeName=text.removeprefix('business '))
        if text == '117, copy, stop. Go ahead. 28, Dollar Tree, Mass, 4, Paul, Henry, Edward, 93. Copy.':
            result.update(placeName='Dollar Tree', street='Route 28')
        if text == 'auto long': result['address'] = 'x' * 501
        content='{}' if text=='invalid' else json.dumps(result)
        response={'done':True, 'done_reason':'length' if text=='truncated' else 'stop','message':{'content':content}}
        self.send_response(200); self.send_header('Content-Type','application/json'); self.end_headers()
        self.wfile.write(json.dumps(response).encode())
backend=http.server.ThreadingHTTPServer(('127.0.0.1',0),Backend)
threading.Thread(target=backend.serve_forever,daemon=True).start()
# Obtain an unused application port; the mock backend keeps its own bound port.
with socket.socket() as probe:
    probe.bind(('127.0.0.1', 0))
    app_port = probe.getsockname()[1]
base_url = f'http://127.0.0.1:{app_port}'
env=dict(os.environ, Address__OllamaUrl=f'http://127.0.0.1:{backend.server_port}',
         ASPNETCORE_ENVIRONMENT='Production', DOTNET_ENVIRONMENT='Production',
         Geocoding__Endpoint=f'http://127.0.0.1:{backend.server_port}/geocoder', Geocoding__TimeoutSeconds='2',
         Geocoding__PlaceEndpoint=f'http://127.0.0.1:{backend.server_port}/places',
         Address__TimeoutSeconds='1', Whisper__WhisperServerBinary='/nonexistent/test-whisper')
with tempfile.TemporaryFile(mode='w+') as log:
    app=subprocess.Popen(['dotnet','bin/Debug/net10.0/OpenScanner.WhisperServer.dll','--urls',base_url],cwd=ROOT,env=env,stdout=log,stderr=log)
    try:
        for _ in range(100):
            try: urllib.request.urlopen(base_url + '/models'); break
            except (urllib.error.URLError, ConnectionError): time.sleep(.1)
        else:
            log.seek(0)
            raise RuntimeError('Server failed to start: ' + log.read())
        with urllib.request.urlopen(base_url + '/swagger/index.html') as response:
            assert 'Swagger UI' in response.read().decode()
        with urllib.request.urlopen(base_url + '/swagger/v1/swagger.json') as response:
            spec = json.load(response)
        assert set(spec['paths']) == {'/health', '/models', '/transcribe', '/address', '/geocode'}
        upload = spec['paths']['/transcribe']['post']['requestBody']['content']['multipart/form-data']['schema']
        assert upload['required'] == ['file']
        assert upload['properties']['file']['format'] == 'binary'
        assert set(upload['properties']) == {'file', 'model', 'prompt'}
        address = spec['paths']['/address']['post']
        assert 'application/json' in address['requestBody']['content']
        assert set(address['responses']) >= {'200', '400', '502', '503', '504'}
        schema_ref = address['responses']['200']['content']['application/json']['schema']['$ref']
        assert set(spec['components']['schemas'][schema_ref.split('/')[-1]]['properties']) == set(fields) | {'geocode'}
        for path in ['/health', '/models', '/transcribe']:
            operation = next(iter(spec['paths'][path].values()))
            assert operation['responses']['200']['content']['application/json']['schema']
        print('PASS: Swagger UI and OpenAPI routes, upload fields, and response schemas in Production')
        def request(payload, route="/address"):
            req=urllib.request.Request(base_url + route,data=json.dumps(payload).encode(),headers={'Content-Type':'application/json'})
            try:
                with urllib.request.urlopen(req) as r: return r.status,json.load(r)
            except urllib.error.HTTPError as e: return e.code,json.load(e)
        for payload in [{},{'text':''},{'text':' '*4},{'text':'x'*2001}]: assert request(payload)[0]==400
        status,value=request({'text':'123 Main St'}); assert status==200 and value['houseNumber']=='123' and value['city'] is None
        assert value['geocode']['latitude'] == 38.9 and value['geocode']['longitude'] == -77.1
        count = len(geocode_calls)
        assert request({'text':'nothing here'})==(200,dict.fromkeys(fields + ['geocode']))
        assert len(geocode_calls) == count
        status, value = request({'text': 'auto long'})
        assert status == 200 and value['geocode'] is None and len(geocode_calls) == count
        for text,status in [('invalid',502),('truncated',502),('unavailable',503),('timeout',504)]:
            assert request({'text':text})[0]==status, text
        def geocode(address, context=None):
            return request({'address': address, 'context': context}, '/geocode')
        for address in [None, '', ' ', 'x'*501]: assert geocode(address)[0] == 400
        assert geocode('123 Main St', 'x'*2001)[0] == 400
        status, result = geocode('123 Main St & test')
        assert status == 200 and result['selectedMatch']['latitude'] == 38.9
        assert result['selectedMatch']['longitude'] == -77.1 and result['selectionStatus'] == 'single_match'
        count = len(geocode_calls)
        assert geocode(' 123 MAIN ST & TEST ')[1] == result and len(geocode_calls) == count
        status, result = geocode('missing')
        assert status == 200 and not result['found'] and result['matches'] == [] and result['selectedMatch'] is None
        count = len(geocode_calls)
        assert geocode('missing')[0] == 200 and len(geocode_calls) == count
        for text, status in [('invalid', 502), ('out-of-range', 502), ('unavailable', 503), ('timeout', 504)]:
            assert geocode(text)[0] == status, text
        # Same cached candidates, different contexts: selections must not leak between requests.
        status, result = geocode('ambiguous', 'South City')
        assert status == 200 and len(result['matches']) == 2 and result['selectedMatchIndex'] == 1
        assert result['selectedMatch']['longitude'] == -77.2 and result['selectionStatus'] == 'llm_selected'
        for context, expected in [(None, 'ambiguous'), ('bad-index', 'llm_unavailable'), ('offline', 'llm_unavailable'), ('slow', 'llm_unavailable')]:
            status, result = geocode('ambiguous', context)
            assert status == 200 and result['selectedMatch'] is None and result['selectionStatus'] == expected
            assert len(result['matches']) == 2
        # Automatic enrichment shares the same resolver and preserves extraction on failure.
        status, value = request({'text': 'auto ambiguous South City'})
        assert status == 200 and value['geocode']['longitude'] == -77.2
        assert value['address'] == 'ambiguous'
        for text in ['auto ambiguous', 'auto missing', 'auto invalid', 'auto unavailable', 'auto timeout']:
            status, value = request({'text': text})
            assert status == 200 and value['geocode'] is None and value['houseNumber'] == '123', text
        # A verified local entry resolves without using fuzzy place search.
        status, value = request({'text': '117, copy, stop. Go ahead. 28, Dollar Tree, Mass, 4, Paul, Henry, Edward, 93. Copy.'})
        assert status == 200 and value['placeName'] == 'Dollar Tree' and value['houseNumber'] == '294'
        assert value['street'] == 'N Broadway' and value['city'] == 'Salem' and value['state'] == 'NH'
        assert value['geocode'] is not None and not place_calls
        status, value = request({'text': 'business Coffee Place'})
        assert status == 200 and value['geocode']['latitude'] == 42.8
        assert value['address'] == '10 Main Street, Salem, New Hampshire, 03079'
        assert value['houseNumber'] == '10' and 'OpenStreetMap' in value['geocode']['attribution']
        count = len(place_calls)
        assert request({'text': 'business Coffee Place'})[1] == value and len(place_calls) == count
        for name in ['missing', 'wrong-town', 'unrelated', 'offline']:
            status, value = request({'text': 'business ' + name})
            assert status == 200 and value['placeName'] == name and value['geocode'] is None, name
        count = len(place_calls) + len(geocode_calls)
        for text in ['217, same traffic. Copy.', '117, clear warning.', 'Copy, I will get my call.']:
            status, value = request({'text': text})
            assert status == 200 and all(v is None for v in value.values())
        assert len(place_calls) + len(geocode_calls) == count
        print('PASS: business directory, strict locality filtering, fuzzy match verification, caching, and no-location skips')
        print('PASS: automatic address geocoding, contextual selection, no-address skip, and failure fallback')
        assert all(b[1] - a[1] >= .95 for a, b in zip(geocode_calls, geocode_calls[1:]))
        print('PASS: geocoding validation, encoding, coordinates, caching, throttling, failures, and LLM selection/fallback')
        print('PASS: 10 endpoint checks (validation, extraction, no address, invalid/truncated JSON, backend failure, timeout)')
    finally: app.terminate(); app.wait(timeout=10); backend.shutdown(); backend.server_close()
