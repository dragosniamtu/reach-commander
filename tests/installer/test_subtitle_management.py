from __future__ import annotations

import importlib
import dataclasses
from datetime import datetime, timezone
import json
import os
import tempfile
import unittest
from unittest import mock
from pathlib import Path

from deploy import render_config

ROOT = Path(__file__).resolve().parents[2]
APP = 'ghcr.io/dragosniamtu/reach-commander@sha256:' + 'a' * 64
WORKER = 'ghcr.io/dragosniamtu/reach-commander@sha256:' + 'b' * 64


class SubtitleManagementTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue((ROOT / 'deploy/subtitle_management.py').is_file(),
                        'The installed subtitle lifecycle manager is missing')
        self.module = importlib.import_module('deploy.subtitle_management')
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.request = render_config.DeploymentRequest.from_mapping({
            'accessMode': 'secure-https', 'bindAddress': '127.0.0.1', 'port': 8092,
            'allowInsecureHttp': False, 'uid': os.geteuid() or 1000 if hasattr(os, 'geteuid') else 1000,
            'gid': os.getegid() or 1000 if hasattr(os, 'getegid') else 1000, 'cpuLimit': '3.0',
            'image': APP, 'sources': [{'id': 'media', 'name': 'Media', 'hostPath': '/srv/media',
                'readOnly': False, 'defaultLeft': True, 'defaultRight': True}]})
        render_config.render_deployment(self.request, ROOT / 'deploy/compose.release.yaml', self.root)
        (self.root / 'lib').mkdir()
        for name in ('compose.subtitles.yaml', 'compose.updater.yaml'):
            (self.root / 'lib' / name).write_bytes((ROOT / 'deploy' / name).read_bytes())
        (self.root / 'compose.override.yaml').write_bytes((ROOT / 'deploy/compose.updater.yaml').read_bytes())
        self.calls = []
        self.label = WORKER
        self.fail_health = False

        def run(args, timeout=30):
            self.calls.append(args)
            if args[:3] == ['docker', 'container', 'ls']:
                return 'reachcommander-subtitles'
            if 'image' in args and 'inspect' in args:
                return 'sha256:' + 'b' * 64 if '{{.Id}}' in args else self.label
            if '{{.Image}}' in args:
                return 'sha256:' + 'b' * 64
            if 'inspect' in args:
                return 'unhealthy' if self.fail_health else 'healthy'
            return ''
        self.run = run
        self.manager = self.module.SubtitleManager(self.root, run=run)

    def test_only_fixed_repository_lowercase_immutable_worker_digest_is_accepted(self):
        self.assertEqual(WORKER, self.manager.worker_image(APP))
        for label in ('', '<no value>', WORKER.upper(), WORKER + '\nother',
                      'ghcr.io/attacker/worker@sha256:' + 'b' * 64,
                      'ghcr.io/dragosniamtu/reach-commander:stable'):
            with self.subTest(label=label):
                self.label = label
                with self.assertRaises(ValueError):
                    self.manager.worker_image(APP)
        with self.assertRaises(ValueError):
            self.manager.worker_image('ghcr.io/attacker/app:latest')

    def test_enabled_overlay_has_only_matching_readonly_sources_and_isolated_runtime(self):
        text = self.manager.render(self.request, WORKER)
        self.assertIn('image: ' + WORKER, text)
        self.assertIn('network_mode: none', text)
        self.assertIn('SUBTITLE_THREADS: "2"', text)
        self.assertIn('mem_limit: 6g', text)
        self.assertIn('source: ./subtitles/mailbox', text)
        self.assertIn("source: '/srv/media'", text)
        self.assertIn("target: '/sources/media'\n        read_only: true", text)
        self.assertNotIn('source: ./data', text)
        self.assertNotIn('docker.sock', text)
        self.assertIn('["/sources/media"]', text)

    def test_missing_label_preflight_does_not_mutate_or_pull_worker(self):
        self.manager.set_enabled(True)
        before = (self.root / 'compose.override.yaml').read_bytes()
        self.label = ''
        with self.assertRaises(ValueError):
            self.manager.preflight(APP)
        self.assertEqual(before, (self.root / 'compose.override.yaml').read_bytes())
        self.assertFalse(any('pull' in call for call in self.calls))

    def test_disabled_installation_accepts_old_images_without_inspecting_worker(self):
        self.label = ''
        self.manager.preflight(APP)
        self.assertEqual([], self.calls)
        self.assertFalse(self.manager.enabled())

    def test_failed_enable_restores_flag_and_overlay(self):
        before = (self.root / 'compose.override.yaml').read_bytes()
        self.fail_health = True
        with self.assertRaises(RuntimeError):
            self.manager.toggle(True)
        self.assertFalse(self.manager.enabled())
        self.assertEqual(before, (self.root / 'compose.override.yaml').read_bytes())
        self.assertTrue(any('stop' in call and 'reachcommander-subtitles' in call for call in self.calls))

    def test_recovery_works_when_enable_flag_was_written_before_overlay(self):
        old = (self.root / 'compose.override.yaml').read_text()
        self.manager.set_enabled(True)
        render_config.atomic_write(self.root / 'state/subtitle-transaction.json',
                                   json.dumps({'enabled': False, 'overlay': old}))
        original = self.manager.run
        def run(arguments, timeout):
            if 'compose' in arguments and 'stop' in arguments and 'subtitle-worker' in arguments:
                raise RuntimeError('no such service: subtitle-worker')
            return original(arguments, timeout)
        self.manager.run = run
        self.manager.recover()
        self.assertFalse(self.manager.enabled())
        self.assertEqual(old, (self.root / 'compose.override.yaml').read_text())
        self.assertFalse(self.manager.marker.exists())

    def test_download_requires_explicit_action_and_only_mounts_model_storage(self):
        self.manager.toggle(True)
        self.assertFalse(any('run' in call for call in self.calls))
        self.write_ready_status()
        self.manager.download_models()
        download = next(call for call in self.calls if 'run' in call)
        self.assertIn(WORKER, download)
        self.assertIn('--romanian', download)
        self.assertIn(f'{self.request.uid}:{self.request.gid}', download)
        self.assertIn('--read-only', download)
        self.assertNotIn('/sources/media', ' '.join(download))
        self.assertNotIn('/data', ' '.join(download))
        self.assertNotIn('/subtitle-work', ' '.join(download))
        self.assertTrue(any('restart' in call and 'subtitle-worker' in call for call in self.calls))

    def write_ready_status(self):
        (self.root / 'subtitles/mailbox/status.json').write_text(json.dumps({
            'version': 1, 'updatedAt': datetime.now(timezone.utc).isoformat(),
            'englishAvailable': True, 'romanianAvailable': True}))
        if os.name == 'posix' and os.geteuid() == 0:
            os.chown(self.root / 'subtitles/mailbox/status.json', self.request.uid, self.request.gid)

    def test_readiness_rejects_stale_or_inconsistent_mailbox_status(self):
        self.manager.prepare_storage()
        self.write_ready_status()
        self.assertEqual((True, True), self.manager.readiness())
        path = self.root / 'subtitles/mailbox/status.json'
        value = json.loads(path.read_text())
        value['updatedAt'] = '2001-01-01T00:00:00Z'
        path.write_text(json.dumps(value))
        self.assertEqual((False, False), self.manager.readiness())
        value['updatedAt'] = datetime.now(timezone.utc).isoformat()
        value['englishAvailable'] = False
        path.write_text(json.dumps(value))
        self.assertEqual((False, False), self.manager.readiness())

    @unittest.skipUnless(hasattr(os, 'O_NONBLOCK'), 'POSIX nonblocking open required')
    def test_readiness_opens_nonblocking_before_rejecting_nonregular_status(self):
        self.manager.prepare_storage()
        self.write_ready_status()
        original = os.open
        def checked_open(path, flags, *args, **kwargs):
            self.assertTrue(flags & os.O_NONBLOCK, 'runtime-owned FIFO must never block a root command')
            return original(path, flags, *args, **kwargs)
        with mock.patch.object(self.module.os, 'open', side_effect=checked_open):
            self.assertEqual((True, True), self.manager.readiness())

    def test_storage_validation_tolerates_removed_atomic_heartbeat_temporary(self):
        self.manager.prepare_storage()
        temporary = self.root / ('subtitles/mailbox/.status.json.' + 'a' * 32 + '.tmp')
        temporary.write_text('{}')
        original = Path.lstat
        def removed(path):
            if path == temporary:
                raise FileNotFoundError('heartbeat replaced temporary')
            return original(path)
        with mock.patch.object(Path, 'lstat', removed):
            self.manager.validate_storage()

    @unittest.skipIf(os.name == 'nt', 'POSIX ownership required')
    def test_reconfigure_rejects_new_runtime_identity_before_deployment_publication(self):
        self.manager.prepare_storage()
        with self.assertRaises(ValueError):
            self.manager.validate_storage(request=dataclasses.replace(self.request, uid=self.request.uid + 1))

    def test_oversized_overlay_is_rejected_before_publishing_recovery_marker(self):
        before = '#' * 131072
        (self.root / 'compose.override.yaml').write_text(before)
        with self.assertRaises(ValueError):
            self.manager.toggle(True)
        self.assertFalse(self.manager.marker.exists())
        self.assertFalse(self.manager.enabled())
        self.assertEqual(before, (self.root / 'compose.override.yaml').read_text())

    def test_download_failure_restarts_offline_worker(self):
        self.manager.toggle(True)
        original = self.manager.run
        def failed_run(arguments, timeout):
            if 'run' in arguments:
                raise RuntimeError('download failed')
            return original(arguments, timeout)
        self.manager.run = failed_run
        with self.assertRaises(RuntimeError):
            self.manager.download_models()
        self.assertTrue(any('restart' in call and 'subtitle-worker' in call for call in self.calls))

    def test_disable_removes_worker_container_and_preserves_model_cache(self):
        self.manager.toggle(True)
        model_directory = self.root / 'subtitles/models/whisper-small'
        model_directory.mkdir()
        model_file = model_directory / 'config.json'
        model_file.write_text('{}')
        if os.name == 'posix' and os.geteuid() == 0:
            for path in (model_directory, model_file):
                os.chown(path, self.request.uid, self.request.gid)
        self.manager.toggle(False)
        self.assertTrue(any('rm' in call and 'subtitle-worker' in call for call in self.calls))
        self.assertEqual('{}', model_file.read_text())
        self.assertFalse(self.manager.enabled())

    def test_storage_rejects_symlinks_and_unexpected_mailbox_entries(self):
        self.manager.prepare_storage()
        self.manager.validate_storage(request=self.request)
        bad = self.root / 'subtitles/mailbox/unexpected'
        bad.write_text('bad')
        with self.assertRaises(ValueError):
            self.manager.validate_storage()
        bad.unlink()
        if os.name != 'nt':
            bad.symlink_to(self.root / '.env')
            with self.assertRaises(ValueError):
                self.manager.validate_storage()

    def test_enabled_state_is_strict_and_pending_transactions_block_preflight(self):
        (self.root / 'state/subtitles-enabled').write_text('yes\n')
        with self.assertRaises(ValueError):
            self.manager.enabled()
        self.manager.set_enabled(True)
        (self.root / 'state/subtitle-transaction.json').write_text('{}')
        with self.assertRaises(ValueError):
            self.manager.preflight(APP)


if __name__ == '__main__':
    unittest.main()
