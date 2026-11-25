import importlib
import io
import json
import subprocess
import tempfile
import unittest
from pathlib import Path


class ManagedSmokeTests(unittest.TestCase):
    def test_administrator_setup_finishes_before_preservation_snapshot(self):
        smoke = importlib.import_module('tools.subtitle_installed_smoke')
        self.assertTrue(hasattr(smoke, 'prepare_administrator'), 'Smoke must finish setup before preserving auth bytes')
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'data/auth').mkdir(parents=True)
            (root / 'data/auth/bootstrap.json').write_text('rotating first-run verifier')
            code = 's' * 43
            calls = []
            class Client:
                def __init__(self, base_url):
                    calls.append(base_url)
                def refresh_antiforgery(self):
                    calls.append('csrf')
                def json_request(self, method, path, payload, *, antiforgery):
                    self_test.assertEqual(('POST', '/api/auth/setup', True), (method, path, antiforgery))
                    self_test.assertEqual(code, payload['setupCode'])
                    (root / 'data/auth/account.json').write_text('stable account fixture')
                    (root / 'data/auth/bootstrap.json').unlink()
            self_test = self
            smoke.prepare_administrator(root, 'http://127.0.0.1:18092', client_factory=Client,
                                        logs_reader=lambda: 'ReachCommander first-run setup code: ' + code)
            self.assertEqual(['http://127.0.0.1:18092', 'csrf'], calls)
            self.assertTrue((root / 'data/auth/account.json').is_file())
            self.assertFalse((root / 'data/auth/bootstrap.json').exists())

    def test_failure_diagnostics_report_state_without_raw_secrets(self):
        smoke = importlib.import_module('tools.subtitle_installed_smoke')
        self.assertTrue(hasattr(smoke, 'failure_diagnostics'), 'Smoke needs redacted Docker failure diagnostics')
        secret = 'private-setup-token-and-password'
        state = {'Status': 'exited', 'Running': False, 'ExitCode': 137, 'OOMKilled': True,
                 'Error': secret, 'Health': {'Status': 'unhealthy', 'FailingStreak': 2,
                                           'Log': [{'ExitCode': 1, 'Output': secret}]}}
        stream = io.StringIO()
        error = subprocess.CalledProcessError(1, ['docker', 'compose'], stderr='permission denied ' + secret)
        smoke.failure_diagnostics('worker activation', error, run=lambda *_: json.dumps(state), output=stream)
        text = stream.getvalue()
        self.assertIn('worker activation', text)
        self.assertIn('137', text)
        self.assertIn('unhealthy', text)
        self.assertIn('permission denied', text)
        self.assertNotIn(secret, text)

    def test_platform_reference_keeps_trusted_repository_and_validates_manifest_digest(self):
        self.assertTrue((Path(__file__).resolve().parents[2] / 'tools/subtitle_installed_smoke.py').exists(),
                        'Real installed-worker smoke gate is missing')
        smoke = importlib.import_module('tools.subtitle_installed_smoke')
        app = 'ghcr.io/dragosniamtu/reach-commander@sha256:' + 'a' * 64
        manifest = {'manifests': [{'digest': 'sha256:' + 'b' * 64,
                                  'platform': {'os': 'linux', 'architecture': 'amd64'}}]}
        self.assertEqual(app[:-64] + 'b' * 64, smoke.platform_reference(app, manifest, 'x86_64'))
        manifest['manifests'][0]['digest'] = 'ghcr.io/attacker/app:latest'
        with self.assertRaises(ValueError):
            smoke.platform_reference(app, manifest, 'x86_64')
