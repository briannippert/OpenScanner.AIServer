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
import tarfile
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
# Units listed in the "active" file are running; start/restart/stop keep it current.
state=root/'active';unit=sys.argv[-1]
active=set(state.read_text().split()) if state.exists() else set()
if sys.argv[1:2]==['is-active']:raise SystemExit(0 if unit in active else 3)
if sys.argv[1:2]==['is-failed']:raise SystemExit(1)
if sys.argv[1:2]==['start'] and os.environ.get('MOCK_START_FAIL')=='1':raise SystemExit(1)
if sys.argv[1:2] in (['start'],['restart']):active.add(unit)
if sys.argv[1:2]==['stop']:active.discard(unit)
state.write_text(' '.join(sorted(active)))
''')
        self.command('sudo', '''
import os,sys,json,subprocess
from pathlib import Path
root=Path(os.environ['MOCK_ROOT']);args=sys.argv[1:]
with (root/'commands.jsonl').open('a') as f:f.write(json.dumps(['sudo']+args)+'\\n')
if args==['-v'] or args[0]=='apt-get':raise SystemExit(0)
if args[0]=='install' and args[-1].startswith('/etc/systemd/system/ollama.service.d'):
    args[-1]=str(root/'etc'/args[-1].lstrip('/'))
elif args[0] in ('install','bash','rm'):
    # The installer may only touch its own (temporary) install root and unit directory.
    if not args[-1].startswith(str(root)) and not args[1].startswith(str(root)):
        raise SystemExit('Unexpected sudo command: '+str(args))
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

    # ---- install.sh, against a stub release tarball ----

    def make_release(self, version='1.0.0'):
        """A release tarball with stub binaries and the real scripts."""
        stage = self.root/f'release-{version}'
        app = stage/'openscanner-ai'
        for d in ['server', 'scripts', 'whisper/bin']: (app/d).mkdir(parents=True)
        (app/'VERSION').write_text(version+'\n')
        (app/'server/appsettings.json').write_text(json.dumps({
            'Logging': {'LogLevel': {'Default': 'Information'}},
            'Geocoding': {'DefaultCity': 'Salem', 'DefaultState': 'NH', 'TimeoutSeconds': 30},
            'Address': {'OllamaUrl': self.origin+'/', 'Model': 'qwen3:4b', 'ReservedVramMb': 3500},
            'Whisper': {'DefaultModel': 'small.en', 'BasePort': 8100}}))
        for stub in ['server/OpenScanner.AIServer', 'whisper/bin/whisper-server', 'whisper/bin/whisper-cli']:
            (app/stub).write_text('#!/bin/sh\nexit 0\n'); (app/stub).chmod(0o755)
        (app/'whisper/VERSION').write_text('v0\n'); (app/'whisper/BACKEND').write_text('cpu\n')
        (app/'whisper/REQUIRES').write_text('\n')
        for name in ['install.sh', 'apply_update.sh', 'uninstall.sh', 'build_whisper.sh', 'setup_ollama.sh']:
            shutil.copy(ROOT/'scripts'/name, app/'scripts'/name)
        tarball = self.root/f'openscanner-ai-{version}.tar.gz'
        with tarfile.open(tarball, 'w:gz') as tar: tar.add(app, arcname='openscanner-ai')
        return tarball

    def run_install(self, *args, tarball=None, **env):
        self.home = self.root/'opt'
        self.units = self.root/'systemd'
        self.units.mkdir(exist_ok=True)
        self.local_config = self.home/'config/appsettings.json'
        self.unit_file = self.units/'openscanner-ai.service'
        return subprocess.run(['bash', str(ROOT/'scripts/install.sh'), '--tarball', str(tarball or self.make_release()),
            '--yes', '--no-model', *args], env=dict(self.env, OPENSCANNER_AI_HOME=str(self.home),
            OPENSCANNER_AI_SYSTEMD_DIR=str(self.units), OPENSCANNER_AI_SETTLE_SECONDS='1', **env),
            stdin=subprocess.DEVNULL, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=120)

    def source_install(self, unit='openscanner-ai'):
        """An install made by the old source installer: a unit that runs out of a checkout."""
        self.units = self.root/'systemd'; self.units.mkdir(exist_ok=True)
        checkout = self.root/'checkout'
        (checkout/'whisper.cpp/models').mkdir(parents=True)
        (checkout/'whisper.cpp/models/ggml-large-v3.bin').write_text('model')
        (checkout/'whisper.cpp/models/for-tests-ggml-tiny.bin').write_text('fixture')
        (checkout/'appsettings.json').write_text(json.dumps({
            'Logging': {'LogLevel': {'Default': 'Debug'}},
            'Geocoding': {'DefaultCity': 'Derry', 'DefaultState': 'NH', 'TimeoutSeconds': 30},
            'Address': {'OllamaUrl': self.origin+'/', 'Model': 'qwen3:4b', 'ReservedVramMb': 0},
            'Whisper': {'DefaultModel': 'large-v3', 'BasePort': 8100, 'ModelsDir': str(checkout/'whisper.cpp/models'),
                        'WhisperServerBinary': str(checkout/'whisper.cpp/build/bin/whisper-server')}}))
        unit_text = (f'[Service]\nUser=someone\nWorkingDirectory={checkout}\n'
            f'ExecStart={checkout}/bin/OpenScanner.AIServer --urls "http://0.0.0.0:8123"\n'
            'Environment=ASPNETCORE_URLS=http://0.0.0.0:8123\n')
        (self.units/f'{unit}.service').write_text(unit_text)
        (self.root/'active').write_text(unit)
        return checkout, unit_text

    def test_fresh_install(self):
        result = self.run_install('--port=8099')
        self.assertEqual(result.returncode, 0, result.stdout)
        config = json.loads(self.local_config.read_text())
        self.assertEqual(config['Whisper']['WhisperServerBinary'], str(self.home/'app/whisper/bin/whisper-server'))
        self.assertEqual(config['Whisper']['ModelsDir'], str(self.home/'whisper/models'))
        self.assertEqual(config['Whisper']['DefaultModel'], 'small.en')
        self.assertIs(config['Whisper']['UseGpu'], False)
        self.assertEqual(config['Address']['Model'], 'qwen3:4b')
        unit = self.unit_file.read_text()
        self.assertIn('Environment=ASPNETCORE_URLS=http://0.0.0.0:8099', unit)
        self.assertIn(f'Environment=OPENSCANNER_AI_HOME={self.home}', unit)
        self.assertIn(f'ExecStart={self.home}/app/server/OpenScanner.AIServer', unit)
        self.assertEqual((self.home/'app/VERSION').read_text().strip(), '1.0.0')
        self.assertFalse((self.home/'staging').exists())
        self.assertIn(['systemctl', 'start', 'openscanner-ai'], self.commands())
        # Nothing was running before, so nothing needed stopping ahead of the swap.
        self.assertNotIn(['sudo', 'systemctl', 'stop', 'openscanner-ai'], self.commands())

    def test_update_keeps_settings_and_previous_release(self):
        self.assertEqual(self.run_install('--port=8123', '--whisper-model=tiny.en', '--llm-model=qwen3:1.7b').returncode, 0)
        result = self.run_install(tarball=self.make_release('2.0.0'))
        self.assertEqual(result.returncode, 0, result.stdout)
        config = json.loads(self.local_config.read_text())
        self.assertEqual(config['Whisper']['DefaultModel'], 'tiny.en')
        self.assertEqual(config['Address']['Model'], 'qwen3:1.7b')
        self.assertIn(':8123', self.unit_file.read_text())
        self.assertEqual((self.home/'app/VERSION').read_text().strip(), '2.0.0')
        self.assertEqual((self.home/'app.prev/VERSION').read_text().strip(), '1.0.0')

    def test_failed_ollama_setup_leaves_running_service_alone(self):
        self.assertEqual(self.run_install('--port=8123').returncode, 0)
        before = (self.local_config.read_text(), self.unit_file.read_text())
        (self.root/'commands.jsonl').unlink()
        self.bad_json = True
        result = self.run_install('--port=9000', '--llm-model=qwen3:1.7b', tarball=self.make_release('2.0.0'))
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn('Configuration was not saved', result.stdout)
        self.assertEqual((self.local_config.read_text(), self.unit_file.read_text()), before)
        self.assertEqual((self.home/'app/VERSION').read_text().strip(), '1.0.0')
        self.assertNotIn(['systemctl', 'stop', 'openscanner-ai'], self.commands())
        self.assertEqual((self.root/'active').read_text().split(), ['ollama', 'openscanner-ai'])

    def test_release_that_does_not_start_is_rolled_back(self):
        self.assertEqual(self.run_install('--port=8123').returncode, 0)
        before = (self.local_config.read_text(), self.unit_file.read_text())
        result = self.run_install('--port=9000', '--whisper-model=tiny.en',
            tarball=self.make_release('2.0.0'), MOCK_START_FAIL='1')
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertEqual((self.home/'app/VERSION').read_text().strip(), '1.0.0')
        self.assertEqual((self.home/'app.failed/VERSION').read_text().strip(), '2.0.0')
        self.assertEqual((self.local_config.read_text(), self.unit_file.read_text()), before)
        self.assertEqual(self.commands()[-1], ['systemctl', 'restart', 'openscanner-ai'])

    def test_migrates_source_install(self):
        # openscanner-whisper is the unit name from before the OpenScanner.AIServer rename.
        checkout, _ = self.source_install('openscanner-whisper')
        result = self.run_install()
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual((self.home/'whisper/models/ggml-large-v3.bin').read_text(), 'model')
        self.assertFalse((checkout/'whisper.cpp/models/ggml-large-v3.bin').exists())
        self.assertFalse((self.home/'whisper/models/for-tests-ggml-tiny.bin').exists())
        config = json.loads(self.local_config.read_text())
        # Only settings that differ from the release defaults are carried over.
        self.assertEqual(config['Geocoding'], {'DefaultCity': 'Derry'})
        self.assertEqual(config['Address']['ReservedVramMb'], 0)
        self.assertEqual(config['Whisper']['DefaultModel'], 'large-v3')
        self.assertEqual(config['Whisper']['ModelsDir'], str(self.home/'whisper/models'))
        self.assertNotIn('Logging', config); self.assertNotIn('BasePort', config['Whisper'])
        self.assertIn(':8123', self.unit_file.read_text())
        self.assertFalse((self.units/'openscanner-whisper.service').exists())
        self.assertEqual((self.root/'active').read_text().split(), ['ollama', 'openscanner-ai'])

    def test_failed_migration_restores_source_install(self):
        checkout, unit_text = self.source_install()
        result = self.run_install(MOCK_START_FAIL='1')
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertEqual((checkout/'whisper.cpp/models/ggml-large-v3.bin').read_text(), 'model')
        self.assertEqual(self.unit_file.read_text(), unit_text)
        self.assertFalse(self.local_config.exists())
        self.assertIn('openscanner-ai', (self.root/'active').read_text().split())

    def test_legacy_wrapper_migrates_its_checkout(self):
        wrapper = (ROOT/'scripts/install_service.sh').read_text()
        self.assertIn('--migrate-from "$(dirname "$SCRIPT_DIR")"', wrapper)

    def test_help_and_conflicting_skip_flags(self):
        help_result = subprocess.run(['bash', str(ROOT/'scripts/install.sh'), '--help'], capture_output=True, text=True)
        self.assertEqual(help_result.returncode, 0)
        self.assertIn('--llm-model', help_result.stdout)
        self.assertNotIn('set -euo', help_result.stdout)
        for args in [['--skip-ollama', '--llm-model=qwen3:4b'], ['--port=70000'], ['--port'], ['--bogus']]:
            result = subprocess.run(['bash', str(ROOT/'scripts/install.sh'), *args], env=self.env, capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0, args)
        self.assertEqual(self.commands(), [])

if __name__ == '__main__': unittest.main(verbosity=2)
