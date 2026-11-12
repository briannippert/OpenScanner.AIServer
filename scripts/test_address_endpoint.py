#!/usr/bin/env python3
"""Exercise /address against mock Ollama; run dotnet build first."""
import http.server, threading, subprocess, os, time, urllib.request, urllib.error, json
import socket
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
fields = ['address','houseNumber','street','unit','city','state','postalCode','country','crossStreet']
class Backend(http.server.BaseHTTPRequestHandler):
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
        assert data['options']['num_ctx']==4096 and data['format']['required']==fields
        text=data['messages'][-1]['content']
        if text=='unavailable': self.send_error(503); return
        if text=='timeout': time.sleep(2); return
        result=dict.fromkeys(fields)
        if text=='123 Main St': result.update(address=text, houseNumber='123', street='Main St')
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
        assert set(spec['paths']) == {'/health', '/models', '/transcribe', '/address'}
        upload = spec['paths']['/transcribe']['post']['requestBody']['content']['multipart/form-data']['schema']
        assert upload['required'] == ['file']
        assert upload['properties']['file']['format'] == 'binary'
        assert set(upload['properties']) == {'file', 'model', 'prompt'}
        address = spec['paths']['/address']['post']
        assert 'application/json' in address['requestBody']['content']
        assert set(address['responses']) >= {'200', '400', '502', '503', '504'}
        schema_ref = address['responses']['200']['content']['application/json']['schema']['$ref']
        assert set(spec['components']['schemas'][schema_ref.split('/')[-1]]['properties']) == set(fields)
        for path in ['/health', '/models', '/transcribe']:
            operation = next(iter(spec['paths'][path].values()))
            assert operation['responses']['200']['content']['application/json']['schema']
        print('PASS: Swagger UI and OpenAPI routes, upload fields, and response schemas in Production')
        def request(payload):
            req=urllib.request.Request(base_url + '/address',data=json.dumps(payload).encode(),headers={'Content-Type':'application/json'})
            try:
                with urllib.request.urlopen(req) as r: return r.status,json.load(r)
            except urllib.error.HTTPError as e: return e.code,json.load(e)
        for payload in [{},{'text':''},{'text':' '*4},{'text':'x'*2001}]: assert request(payload)[0]==400
        status,value=request({'text':'123 Main St'}); assert status==200 and value['houseNumber']=='123' and value['city'] is None
        assert request({'text':'nothing here'})==(200,dict.fromkeys(fields))
        for text,status in [('invalid',502),('truncated',502),('unavailable',503),('timeout',504)]:
            assert request({'text':text})[0]==status, text
        print('PASS: 10 endpoint checks (validation, extraction, no address, invalid/truncated JSON, backend failure, timeout)')
    finally: app.terminate(); app.wait(timeout=10); backend.shutdown(); backend.server_close()
