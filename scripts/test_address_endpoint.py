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
        if urlparse(self.path).path == '/streets':
            self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers()
            self.wfile.write(b'{"features": []}')
            return
        if urlparse(self.path).path == '/places':
            query_text = query['q'][0]
            assert query_text.endswith(', USA')
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
        if address.startswith('16 Garabedian'):
            matches[0]['matchedAddress'] = '16 GARABEDIAN DR, SALEM, NH, 03079'
        if address.startswith('16 GarabedianWrong'):
            matches[0]['matchedAddress'] = '16 GARABEDIAN DR, SALEM, NH, 03079'
        if address.startswith(('8 Graymoor Woods', '9 Graymoor Woods', '8 N Graymoor Woods', '8 Elsewhere Woods')):
            matches[0]['matchedAddress'] = '8 BRAEMOOR WOODS RD, SALEM, NH, 03079'
        if address.startswith('18 Graymoor Woods'):
            matches = [
                {'matchedAddress': '18 BRAEMOOR WOODS RD, SALEM, NH, 03079', 'coordinates': {'x': -71.2, 'y': 42.7}},
                {'matchedAddress': '18 BRAEMOR WOODS RD, SALEM, NH, 03079', 'coordinates': {'x': -71.21, 'y': 42.71}}]
        if address.startswith('44 Millville Street'):
            matches[0]['matchedAddress'] = '44 MILLVILLE ST, SALEM, NH, 03079'
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
        transmission = 'callType' in data['format']['required']
        if not ranking and not transmission: assert data['format']['required']==fields
        text=data['messages'][-1]['content']
        if transmission:
            agency = text.splitlines()[0].split(':', 1)[1].strip()
            choices = data['format']['properties']['callType']['enum']
            services = data['format']['properties']['service']['enum']
            if agency == 'police':
                assert 'alarm' not in choices and 'fire' not in choices and 'security_alarm' in choices
                assert services == ['police', None]
            if agency == 'fire':
                assert 'security_alarm' not in choices and 'traffic_stop' not in choices and 'welfare_check' not in choices
                assert services == ['fire', None]
            text = text.split('Transmission text:\n', 1)[1]
            if text == 'unavailable': self.send_error(503); return
            if text == 'timeout': time.sleep(2); return
            result = dict.fromkeys(fields + ['callType', 'service', 'summary', 'incidentStatus'])
            if text == 'traffic stop':
                result.update(callType='traffic_stop', service='police', summary='Traffic stop at 123 Main St.',
                              address='123 Main St', houseNumber='123', street='Main St')
            if text == 'medical':
                result.update(callType='medical', summary='Patient having trouble breathing.')
            if text == 'default probe':
                result.update(callType='other', summary='Incident at 124 Main St.', address='124 Main St',
                              houseNumber='124', street='Main St')
            if text == 'explicit locality':
                result.update(callType='medical', summary='Medical call at 9 Pine St, Concord, NH.', address='9 Pine St',
                              houseNumber='9', street='Pine St', city='Concord', state='NH')
            if text.startswith('business '): result.update(callType='other', summary='Incident at a business.', placeName=text.removeprefix('business '))
            if text == 'welfare policy': result.update(callType='welfare_check', service='fire', summary='Check on a person.')
            if text == 'alarm policy': result.update(callType='alarm', service='fire', summary='Residential motion alarm at 43 Woodmeadow Drive.', address='43 Woodmeadow Drive', houseNumber='43', street='Woodmeadow Drive')
            if text == 'fire policy': result.update(callType='fire', service='fire', summary='Reported fire.')
            if text == 'medical policy': result.update(callType='medical', service='fire', summary='Medical complaint.')
            if text == 'security policy': result.update(callType='security_alarm', service='police', summary='Security alarm.')
            if text == 'bad-category': result.update(callType='invented', summary='Test')
            content = '{}' if text == 'invalid' else json.dumps(result)
            self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers()
            self.wfile.write(json.dumps({'done': True, 'message': {'content': content}}).encode())
            return
        if ranking:
            selection = json.loads(text)
            context = selection['context']
            if context == 'offline': self.send_error(503); return
            if context == 'slow': time.sleep(3); return
            if selection['address'].startswith('Coffee Place'):
                assert len(selection['candidates']) == 1
                assert 'New Hampshire' in selection['candidates'][0]['address']
            selected = 0 if context == 'correction ambiguous' else 1 if context in ('South City', 'auto ambiguous South City') else (99 if context == 'bad-index' else (0 if context == 'business Coffee Place' else None))
            self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers()
            self.wfile.write(json.dumps({'done': True, 'message': {'content': json.dumps({'selectedMatchIndex': selected})}}).encode())
            return
        if text=='unavailable': self.send_error(503); return
        if text=='timeout': time.sleep(2); return
        result=dict.fromkeys(fields)
        if text=='123 Main St': result.update(address=text, houseNumber='123', street='Main St')
        if text in ('guard invented place', 'Coffee Place at 44 Millville Street'):
            result.update(address='44 Millville Street', houseNumber='44', street='Millville Street',
                placeName='Dollar Tree' if text == 'guard invented place' else 'Coffee Place')
        if text == 'partial street': result.update(address='16 Garabedian', houseNumber='16', street='Garabedian')
        if text == 'wrong partial street': result.update(address='16 GarabedianWrong', houseNumber='16', street='GarabedianWrong')
        if text == 'wrong explicit suffix': result.update(address='16 Garabedian Street', houseNumber='16', street='Garabedian Street')
        if text.startswith('correction '):
            source_house = '9' if text == 'correction number' else ('18' if text == 'correction ambiguous' else '8')
            source_street = {'correction suffix': 'Graymoor Woods Drive', 'correction direction': 'N Graymoor Woods',
                             'correction unrelated': 'Elsewhere Woods'}.get(text, 'Graymoor Woods')
            result.update(address=f'{source_house} {source_street}', houseNumber=source_house, street=source_street, unit='302')
        if text == 'guard place only': result['placeName'] = 'Dollar Tree'
        if text == 'guard wrong number': result.update(address='44 Mismatch Street', houseNumber='44', street='Mismatch Street')
        if text == 'guard wrong street': result.update(address='123 Mismatch Street', houseNumber='123', street='Mismatch Street')

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
assets = tempfile.TemporaryDirectory()
fixture_dir = Path(assets.name)
transcript_file = fixture_dir / 'transcript.txt'
transcript_file.write_text('traffic stop')
(fixture_dir / 'ggml-test.bin').write_bytes(b'fixture')
whisper = fixture_dir / 'whisper-server'
whisper.write_text('''#!/usr/bin/env python3
import http.server, json, os, sys
from pathlib import Path
port = int(sys.argv[sys.argv.index('--port')+1])
class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_GET(self):
        self.send_response(200); self.end_headers(); self.wfile.write(b'{"status":"ok"}')
    def do_POST(self):
        self.rfile.read(int(self.headers['Content-Length']))
        text = Path(os.environ['MOCK_TRANSCRIPT_FILE']).read_text()
        if text == '__CRASH__': os._exit(1)
        if text == '__HTTP500__':
            self.send_response(500); self.end_headers(); self.wfile.write(b'boom'); return
        self.send_response(200); self.send_header('Content-Type','application/json'); self.end_headers()
        self.wfile.write(json.dumps({'text':text}).encode())
http.server.HTTPServer(('127.0.0.1',port),Handler).serve_forever()
''')
whisper.chmod(0o755)
with socket.socket() as probe:
    probe.bind(('127.0.0.1',0)); whisper_port = probe.getsockname()[1]
env=dict(os.environ, Address__OllamaUrl=f'http://127.0.0.1:{backend.server_port}',
         ASPNETCORE_ENVIRONMENT='Production', DOTNET_ENVIRONMENT='Production',
         Geocoding__Endpoint=f'http://127.0.0.1:{backend.server_port}/geocoder', Geocoding__TimeoutSeconds='2',
         Geocoding__PlaceEndpoint=f'http://127.0.0.1:{backend.server_port}/places',
         Geocoding__StreetEndpoint=f'http://127.0.0.1:{backend.server_port}/streets',
         Address__TimeoutSeconds='1', Whisper__WhisperServerBinary=str(whisper),
         Whisper__ModelsDir=str(fixture_dir), Whisper__DefaultModel='test', Whisper__BasePort=str(whisper_port),
         Whisper__UseGpu='false', MOCK_TRANSCRIPT_FILE=str(transcript_file))
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
        assert set(spec['paths']) == {'/health', '/models', '/transcribe', '/address', '/geocode', '/transmission'}
        transmission_schema = spec['paths']['/transmission']['post']['responses']['200']['content']['application/json']['schema']
        assert len(transmission_schema['oneOf']) == 2
        assert transmission_schema['oneOf'][1]['maxProperties'] == 0
        upload = spec['paths']['/transcribe']['post']['requestBody']['content']['multipart/form-data']['schema']
        assert upload['required'] == ['file']
        assert upload['properties']['file']['format'] == 'binary'
        assert set(upload['properties']) == {'file', 'model', 'prompt', 'includeMetadata', 'service', 'defaultCity', 'defaultState'}
        assert upload['properties']['includeMetadata']['type'] == 'boolean'
        assert len(spec['paths']['/transcribe']['post']['responses']['200']['content']['application/json']['schema']['oneOf']) == 2
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
        def transmission(text, service=None):
            return request({'text': text, 'service': service}, '/transmission')
        count = len(geocode_calls) + len(place_calls)
        for text in ['copy', "I'm out", 'roger']:
            assert transmission(text, 'police') == (200, {})
        assert len(geocode_calls) + len(place_calls) == count
        assert transmission('', 'fire')[0] == 400
        assert transmission('test', 'ems')[0] == 400
        assert transmission('welfare policy', 'fire')[1]['callType'] == 'public_assistance'
        assert transmission('welfare policy', 'police')[1]['callType'] == 'welfare_check'
        assert transmission('welfare policy')[1]['callType'] == 'public_assistance'
        result = transmission('alarm policy', 'police')[1]
        assert result['service'] == 'police' and result['callType'] == 'security_alarm'
        assert result['location']['houseNumber'] == '43'
        assert transmission('alarm policy', 'fire')[1]['callType'] == 'alarm'
        assert transmission('security policy', 'fire')[1]['callType'] == 'other'
        assert transmission('fire policy', 'police')[1]['callType'] == 'other'
        assert transmission('medical policy', 'police')[1]['callType'] == 'other'
        assert transmission('medical policy', 'fire')[1]['callType'] == 'medical'
        assert transmission('traffic stop', 'police')[1]['callType'] == 'traffic_stop'
        status, value = transmission('traffic stop', ' FIRE ')
        assert status == 200 and value['service'] == 'fire' and value['callType'] == 'other'
        assert value['location']['houseNumber'] == '123' and value['location']['geocode']['latitude'] == 38.9
        status, value = transmission('medical')
        assert status == 200 and value['callType'] == 'medical' and value['service'] is None
        assert all(v is None for v in value['location'].values())
        for text, expected in [('invalid', 502), ('bad-category', 502), ('unavailable', 503), ('timeout', 504)]:
            assert transmission(text)[0] == expected
        print('PASS: transmission metadata, service override, empty chatter, location enrichment, validation and failures')
        # Per-request defaults apply to lookups only and never leak into another request.
        for route, text in [('/transmission', 'traffic stop'), ('/address', '123 Main St')]:
            for city in (['Derry', 'Windham'] if route == '/transmission' else ['Hudson', 'Londonderry']):
                count = len(geocode_calls)
                status, value = request({'text': text, 'defaultCity': city, 'defaultState': 'NH'}, route)
                assert status == 200 and len(geocode_calls) == count + 1
                assert geocode_calls[-1][0].endswith(', ' + city + ', NH')
                assert (value['location'] if route == '/transmission' else value)['city'] is None
        assert request({'text': 'default probe'}, '/transmission')[0] == 200
        assert geocode_calls[-1][0] == '124 Main St, Salem, NH'
        status, value = request({'text': 'explicit locality', 'defaultCity': 'Derry', 'defaultState': 'MA'}, '/transmission')
        assert status == 200 and geocode_calls[-1][0] == '9 Pine St, Concord, NH'
        assert value['location']['city'] == 'Concord'
        status, value = request({'text': 'business Coffee Place', 'defaultCity': 'Concord', 'defaultState': 'NH'}, '/transmission')
        assert status == 200 and value['location']['city'] == 'Concord'
        for route in ['/address', '/transmission']:
            assert request({'text': 'test', 'defaultCity': 'x'*101}, route)[0] == 400
            assert request({'text': 'test', 'defaultState': 'NH, MA'}, route)[0] == 400
        def transcribe(text, **options):
            transcript_file.write_text(text)
            boundary = 'openscanner-test-boundary'
            parts = []
            for name, value in options.items():
                parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n')
            parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="sample.wav"\r\nContent-Type: audio/wav\r\n\r\nfixture\r\n--{boundary}--\r\n')
            req = urllib.request.Request(base_url+'/transcribe', data=''.join(parts).encode(),
                headers={'Content-Type': 'multipart/form-data; boundary='+boundary})
            try:
                with urllib.request.urlopen(req, timeout=15) as response: return response.status, json.load(response)
            except urllib.error.HTTPError as error: return error.code, json.load(error)
        assert transcribe('traffic stop') == (200, {'text':'traffic stop'})
        status, result = transcribe('traffic stop', includeMetadata='true', service='police', defaultCity='Pelham', defaultState='NH')
        assert status == 200 and result['metadata']['callType'] == 'traffic_stop' and result['metadataError'] is None
        assert geocode_calls[-1][0] == '123 Main St, Pelham, NH'
        assert result['metadata']['location']['city'] is None
        for text in ['copy', 'roger', '']:
            assert transcribe(text, includeMetadata='true')[1] == {'text':text, 'metadata':{}, 'metadataError':None}
        for text in ['unavailable', 'invalid', 'timeout', 'x'*2001]:
            status, result = transcribe(text, includeMetadata='true')
            assert status == 200 and result['text'] == text and result['metadata'] is None and result['metadataError']
        assert transcribe('copy', includeMetadata='maybe')[0] == 400
        assert transcribe('copy', service='ems')[0] == 400
        assert transcribe('copy', defaultCity='x'*101)[0] == 400
        # Failures must be distinguishable from silence, never a 200 with empty text.
        assert transcribe('copy', model='not-installed')[0] == 400
        assert transcribe('copy', model='../escape')[0] == 400
        status, result = transcribe('__HTTP500__')
        assert status == 502 and 'error' in result, (status, result)
        # A crashed whisper-server child is replaced on the next request.
        status, result = transcribe('__CRASH__')
        assert status == 502 and 'error' in result, (status, result)
        assert transcribe('traffic stop') == (200, {'text':'traffic stop'})
        # Blank service means "not supplied" on every endpoint.
        assert transcribe('copy', service='')[0] == 200
        assert request({'text': 'copy', 'service': ''}, '/transmission')[0] == 200
        print('PASS: whisper model/inference failures, crash recovery, and blank service handling')
        print('PASS: request locality precedence/isolation, optional audio metadata, text preservation, validation and failures')
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
        directory_place_count = len(place_calls)
        status, value = request({'text': '117, copy, stop. Go ahead. 28, Dollar Tree, Mass, 4, Paul, Henry, Edward, 93. Copy.'})
        assert status == 200 and value['placeName'] == 'Dollar Tree' and value['houseNumber'] == '294'
        assert value['street'] == 'N Broadway' and value['city'] == 'Salem' and value['state'] == 'NH'
        assert value['geocode'] is not None and len(place_calls) == directory_place_count
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
        place_count = len(place_calls)
        for text in ['guard invented place', 'Coffee Place at 44 Millville Street']:
            status, result = request({'text': text})
            assert status == 200 and result['houseNumber'] == '44' and result['street'] == 'Millville Street'
            assert result['address'] == '44 Millville Street'
            assert result['geocode']['matchedAddress'] == '44 MILLVILLE ST, SALEM, NH, 03079'
        assert len(place_calls) == place_count
        count = len(geocode_calls)
        status, result = request({'text': 'guard place only'})
        assert status == 200 and result['placeName'] is None and result['geocode'] is None
        assert len(geocode_calls) == count and len(place_calls) == place_count
        for text in ['guard wrong number', 'guard wrong street']:
            status, result = request({'text': text})
            assert status == 200 and result['geocode'] is None
            assert result['address'].endswith('Mismatch Street')
        status, result = request({'text': 'partial street'})
        assert status == 200 and result['street'] == 'Garabedian'
        assert result['geocode']['matchedAddress'] == '16 GARABEDIAN DR, SALEM, NH, 03079'
        for text in ['wrong partial street', 'wrong explicit suffix']:
            status, result = request({'text': text})
            assert status == 200 and result['geocode'] is None
        status, result = request({'text': 'correction valid'})
        assert status == 200 and result['address'] == '8 Graymoor Woods' and result['unit'] == '302'
        assert result['geocode']['matchedAddress'] == '8 BRAEMOOR WOODS RD, SALEM, NH, 03079'
        for text in ['correction number', 'correction suffix', 'correction direction', 'correction unrelated', 'correction ambiguous']:
            status, result = request({'text': text})
            assert status == 200 and result['geocode'] is None, (text, result)
        for defaults in [{'defaultCity': 'Concord'}, {'defaultState': 'MA'}]:
            status, result = request({'text': 'correction valid'} | defaults)
            assert status == 200 and result['geocode'] is None, result
        print('PASS: combined street-name/suffix correction preserves number, locality, direction, explicit suffix and ambiguity checks')
        print('PASS: omitted street suffix accepted without changing a supplied suffix or street name')
        print('PASS: invented business rejection, explicit street precedence, and mismatched number/street rejection')
        print('PASS: 10 endpoint checks (validation, extraction, no address, invalid/truncated JSON, backend failure, timeout)')
    finally: app.terminate(); app.wait(timeout=10); backend.shutdown(); backend.server_close(); assets.cleanup()
