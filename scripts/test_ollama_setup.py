#!/usr/bin/env python3
"""Exercise installer provisioning with mock system commands and a local Ollama API.

No packages, real services, real models, or /etc files are changed.
"""
import http.server
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import unittest

ROOT = Path(__file__).resolve().parents[1]
REAL_CURL = shutil.which('curl')

class SetupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        self.calls = []
        self.installed = True
        self.old = False
        self.pull_error = False
        self.bad_json = False
        owner = self
        class API(http.server.BaseHTTPRequestHandler):
            def log_message(self, *args): pass
            def reply(self, code, value):
                self.send_response(code); self.send_header('Content-Type', 'application/json'); self.end_headers()
                self.wfile.write(json.dumps(value).encode())
            def do_GET(self): self.reply(200, {'version': '0.40.16'})
            def do_POST(self):
                data = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                owner.calls.append((self.path, data))
                if self.path == '/api/show':
                    self.reply(200 if owner.installed else 404,
                        {'details': {}} if owner.installed else {'error': 'model not found'})
                elif self.path == '/api/pull':
                    self.assert_payload(data)
                    if owner.old and not (owner.root/'upgraded').exists():
                        self.reply(412, {'error': 'The model requires a newer version of Ollama.'})
                    elif owner.pull_error:
                        self.reply(500, {'error': 'download failed'})
                    else:
                        owner.installed = True
                        self.reply(200, {'status': 'success'})
                elif self.path == '/api/chat':
                    owner.assertFalse(data['think']); owner.assertFalse(data['stream'])
                    owner.assertEqual(data['keep_alive'], 0)
                    owner.assertEqual(data['options']['num_ctx'], 4096)
                    owner.assertEqual(data['format']['required'], ['ready'])
                    owner.assertEqual(data['format']['properties']['ready']['enum'], [True])
                    self.reply(200, {'done': True, 'done_reason': 'stop', 'message': {
                        'content': 'invalid' if owner.bad_json else '{"ready":true}'}})
                else:
                    self.reply(404, {'error': 'unexpected request'})
            def assert_payload(self, data): owner.assertFalse(data['stream'])
        self.server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), API)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.origin = f'http://127.0.0.1:{self.server.server_port}'
        self.config = self.root/'appsettings.json'
        self.config.write_text(json.dumps({'Address': {'Model': 'qwen3:4b', 'OllamaUrl': self.origin+'/',
            'ReservedVramMb': 3500}, 'Whisper': {'DefaultModel': 'small.en'}}))
        self.original = self.config.read_text()
        self.env = dict(os.environ, PATH=str(self.bin)+os.pathsep+os.environ['PATH'],
            MOCK_ROOT=str(self.root), MOCK_ORIGIN=self.origin, REAL_CURL=REAL_CURL)
        self.env.pop('Address__Model', None); self.env.pop('Address__OllamaUrl', None)
        self.command('ollama', 'raise SystemExit(0)')
        self.command('nvidia-smi', 'raise SystemExit(1)')
        self.command('dotnet', 'print("10.0.100")')
        self.command('systemctl', '''
import os,sys,json
from pathlib import Path
root=Path(os.environ['MOCK_ROOT'])
with (root/'commands.jsonl').open('a') as f:f.write(json.dumps(['systemctl']+sys.argv[1:])+'\\n')
if sys.argv[1:3]==['cat','ollama.service'] and os.environ.get('MOCK_MISSING_SERVICE')=='1':raise SystemExit(1)
if sys.argv[1:3]==['restart','ollama'] and os.environ.get('MOCK_RESTART_FAIL')=='1':raise SystemExit(1)
''')
        self.command('sudo', '''
import os,sys,json,subprocess
from pathlib import Path
root=Path(os.environ['MOCK_ROOT']);args=sys.argv[1:]
with (root/'commands.jsonl').open('a') as f:f.write(json.dumps(['sudo']+args)+'\\n')
if args==['-v'] or args[0]=='apt-get':raise SystemExit(0)
if args[0]=='install':
    assert args[-1].startswith('/etc/systemd/system/ollama.service.d')
    args[-1]=str(root/'etc'/args[-1].lstrip('/'))
elif args[0]!='systemctl':raise SystemExit('Unexpected sudo command: '+str(args))
raise SystemExit(subprocess.call(args))
''')
        self.command('curl', '''
import os,sys
from pathlib import Path
args=sys.argv[1:]
if 'https://ollama.com/install.sh' in args:
    destination=args[args.index('-o')+1]
    Path(destination).write_text('#!/bin/sh\\nprintf "install\\\\n" >> "$MOCK_ROOT/install.log"\\ntouch "$MOCK_ROOT/upgraded"\\n')
    raise SystemExit(0)
args=[a.replace('https://remote.example:11434',os.environ['MOCK_ORIGIN']) for a in args]
os.execv(os.environ['REAL_CURL'],[os.environ['REAL_CURL']]+args)
''')

    def command(self, name, code):
        path = self.bin/name
        path.write_text('#!/usr/bin/env python3\n'+code+'\n')
        path.chmod(0o755)

    def tearDown(self):
        self.server.shutdown(); self.server.server_close(); self.temp.cleanup()

    def run_setup(self, model='qwen3:4b', url='', upgrade='false', input=''):
        script = '''set -euo pipefail
source "$1"
log_info() { echo "$*"; }
log_success() { echo "$*"; }
log_error() { echo "$*" >&2; }
setup_ollama "$2" "$3" "$4" "$5"
'''
        return subprocess.run(['bash', '-c', script, 'setup-test', str(ROOT/'scripts/setup_ollama.sh'),
            str(self.config), model, url, upgrade], env=self.env, input=input, text=True,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=20)

    def commands(self):
        log = self.root/'commands.jsonl'
        return [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []

    def assert_success(self, result):
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn('ready and verified', result.stdout)

    def test_existing_model_skips_download_and_sets_gpu_limits(self):
        self.assert_success(self.run_setup())
        self.assertEqual([p for p, _ in self.calls], ['/api/show', '/api/chat'])
        self.assertFalse((self.root/'install.log').exists())
        dropin = (self.root/'etc/etc/systemd/system/ollama.service.d/openscanner.conf').read_text()
        self.assertIn('OLLAMA_NUM_PARALLEL=1', dropin)
        self.assertIn('OLLAMA_MAX_LOADED_MODELS=1', dropin)
        self.assertIn('OLLAMA_HOST=127.0.0.1:', dropin)
        self.assertEqual(json.loads(self.config.read_text())['Address']['ReservedVramMb'], 3500)

    def test_first_install_downloads_model(self):
        self.installed = False
        self.env['MOCK_MISSING_SERVICE'] = '1'
        self.assert_success(self.run_setup())
        self.assertEqual((self.root/'install.log').read_text().count('install'), 1)
        self.assertIn('/api/pull', [p for p, _ in self.calls])

    def test_old_ollama_upgrades_once_and_retries(self):
        self.installed = False; self.old = True
        self.assert_success(self.run_setup())
        self.assertEqual((self.root/'install.log').read_text().count('install'), 1)
        self.assertEqual([p for p, _ in self.calls].count('/api/pull'), 2)

    def test_explicit_upgrade(self):
        self.assert_success(self.run_setup(upgrade='true'))
        self.assertTrue((self.root/'upgraded').exists())

    def test_remote_model_uses_api_without_local_mutations(self):
        self.installed = False
        self.assert_success(self.run_setup(url='https://remote.example:11434'))
        self.assertEqual(self.commands(), [])
        self.assertEqual(json.loads(self.config.read_text())['Address']['OllamaUrl'], 'https://remote.example:11434/')

    def test_remote_upgrade_is_not_attempted(self):
        self.installed = False; self.old = True
        result = self.run_setup(url='https://remote.example:11434')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.commands(), [])
        self.assertEqual(self.config.read_text(), self.original)

    def test_failed_download_does_not_save_configuration(self):
        self.installed = False; self.pull_error = True
        self.assertNotEqual(self.run_setup(model='qwen3:1.7b').returncode, 0)
        self.assertEqual(self.config.read_text(), self.original)

    def test_invalid_model_output_does_not_save_configuration(self):
        self.bad_json = True
        self.assertNotEqual(self.run_setup(model='qwen3:1.7b').returncode, 0)
        self.assertEqual(self.config.read_text(), self.original)

    def test_service_failure_stops_before_model_requests(self):
        self.env['MOCK_RESTART_FAIL'] = '1'
        self.assertNotEqual(self.run_setup().returncode, 0)
        self.assertEqual(self.calls, [])

    def test_invalid_model_or_url_cannot_trigger_install(self):
        for model, url in [('bad model', ''), ('qwen3:4b', 'http://localhost:11434/prefix'),
                           ('qwen3:4b', 'http://localhost:80')]:
            self.assertNotEqual(self.run_setup(model=model, url=url).returncode, 0)
        self.assertEqual(self.commands(), [])

    def test_keep_or_select_model_interactively(self):
        self.assert_success(self.run_setup(model='', input='\n'))
        self.assertEqual(json.loads(self.config.read_text())['Address']['Model'], 'qwen3:4b')
        self.assert_success(self.run_setup(model='', input='2\n'))
        self.assertEqual(json.loads(self.config.read_text())['Address']['Model'], 'qwen3:1.7b')

    def test_installer_restores_existing_service_after_setup_failure(self):
        project = self.root/'project'
        (project/'scripts').mkdir(parents=True)
        for name in ['install_service.sh', 'setup_ollama.sh']:
            shutil.copy(ROOT/'scripts'/name, project/'scripts'/name)
        shutil.copy(self.config, project/'appsettings.json')
        self.bad_json = True
        result = subprocess.run(['bash', str(project/'scripts/install_service.sh'), '--skip-update',
            '--port=8099', '--llm-model=qwen3:4b'], env=self.env, input='\n', text=True,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=20)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn(['systemctl', 'stop', 'openscanner-whisper'], self.commands())
        self.assertIn(['systemctl', 'start', 'openscanner-whisper'], self.commands())
        self.assertIn('Configuration was not saved', result.stdout)

    def test_help_and_conflicting_skip_flags(self):
        help_result = subprocess.run(['bash', str(ROOT/'scripts/install_service.sh'), '--help'], capture_output=True, text=True)
        self.assertEqual(help_result.returncode, 0)
        self.assertIn('--llm-model', help_result.stdout)
        result = subprocess.run(['bash', str(ROOT/'scripts/install_service.sh'), '--skip-ollama', '--llm-model=qwen3:4b'],
            env=self.env, capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.commands(), [])

if __name__ == '__main__': unittest.main(verbosity=2)
