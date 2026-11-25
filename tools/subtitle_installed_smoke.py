#!/usr/bin/env python3
"""Real Docker managed-subtitle smoke gate on a disposable Linux runner.

Requires root, Docker Compose/buildx, and an anonymously pullable immutable app image.
Never touches /opt/reachcommander or systemd. Refuses existing production container names.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import uuid

sys.dont_write_bytecode = True
REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO))
from deploy import render_config as renderer
from deploy.source_management import SourceTransaction
from deploy.subtitle_management import APP_PATTERN, SubtitleManager, run_command
from deploy.updater_protocol import SOURCE_MANAGEMENT_PROTOCOL_VERSION
from tools.container_media_preview_smoke import SmokeClient, USERNAME, PASSWORD


def platform_reference(app, manifest, machine):
    if not APP_PATTERN.fullmatch(app):
        raise ValueError('Smoke requires the fixed application repository and immutable digest')
    architecture = {'x86_64': 'amd64', 'aarch64': 'arm64'}.get(machine, machine)
    matches = [entry.get('digest') for entry in manifest.get('manifests', [])
               if entry.get('platform', {}).get('os') == 'linux'
               and entry.get('platform', {}).get('architecture') == architecture]
    if len(matches) != 1 or not isinstance(matches[0], str) or not re.fullmatch(r'sha256:[a-f0-9]{64}', matches[0]):
        raise ValueError('Published manifest has no unique trusted Linux platform digest')
    return renderer.REPOSITORY + '@' + matches[0]


def protocol_request(action, **fields):
    return json.dumps({'protocolVersion': SOURCE_MANAGEMENT_PROTOCOL_VERSION,
                       'requestId': str(uuid.uuid4()), 'action': action, **fields}).encode()


def private_startup_logs():
    result = subprocess.run(['docker', 'logs', '--tail', '200', 'reachcommander'], check=True,
                            timeout=30, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    return result.stdout


def prepare_administrator(root, base_url, *, client_factory=SmokeClient, logs_reader=private_startup_logs):
    # Bootstrap verifiers deliberately rotate on each unauthenticated restart. Finish
    # the supported setup flow before asserting that durable account/key bytes survive.
    codes = re.findall(r'ReachCommander first-run setup code: ([A-Za-z0-9_-]{43})(?![A-Za-z0-9_-])', logs_reader())
    if not codes:
        raise RuntimeError('First-run setup code was unavailable')
    client = client_factory(base_url)
    client.refresh_antiforgery()
    client.json_request('POST', '/api/auth/setup', {
        'setupCode': codes[-1], 'username': USERNAME, 'password': PASSWORD,
    }, antiforgery=True)
    if not (root / 'data/auth/account.json').is_file() or (root / 'data/auth/bootstrap.json').exists():
        raise RuntimeError('First-run administrator setup did not complete')


def failure_diagnostics(stage, error, *, run=run_command, output=None):
    """Emit only enum/boolean/numeric Docker fields; never raw logs or health output."""
    output = output or sys.stderr
    failure = {'stage': stage, 'errorType': type(error).__name__}
    if isinstance(error, subprocess.CalledProcessError):
        failure['commandExitCode'] = error.returncode
        text = error.stderr if isinstance(error.stderr, str) else ''
        failure['dockerErrorCategories'] = [category for category in (
            'permission denied', 'no such file', 'not found', 'port is already allocated',
            'pull access denied', 'unhealthy', 'out of memory', 'no space left',
        ) if category in text.lower()]
    print('Managed subtitle smoke failure: ' + json.dumps(failure), file=output, flush=True)
    for name in ('reachcommander', 'reachcommander-subtitles'):
        try:
            raw = run(['docker', 'inspect', '--format', '{{json .State}}', name], 15)
            if len(raw) > 131072:
                raise ValueError('Docker state exceeds diagnostic limit')
            state = json.loads(raw)
            if type(state) is not dict:
                raise ValueError('Invalid Docker state')
            summary = {'container': name, 'status': state.get('Status') if state.get('Status') in (
                'created', 'running', 'paused', 'restarting', 'removing', 'exited', 'dead') else 'unknown'}
            for field in ('Running', 'OOMKilled'):
                if type(state.get(field)) is bool:
                    summary[field] = state[field]
            if type(state.get('ExitCode')) is int:
                summary['ExitCode'] = state['ExitCode']
            health = state.get('Health', {})
            if type(health) is dict:
                if health.get('Status') in ('starting', 'healthy', 'unhealthy'):
                    summary['health'] = health['Status']
                if type(health.get('FailingStreak')) is int:
                    summary['healthFailingStreak'] = health['FailingStreak']
                if type(health.get('Log')) is list:
                    summary['recentHealthExitCodes'] = [entry['ExitCode'] for entry in health['Log'][-3:]
                        if type(entry) is dict and type(entry.get('ExitCode')) is int]
            print(json.dumps(summary), file=output, flush=True)
        except Exception as diagnostic_error:
            print(json.dumps({'container': name, 'diagnostics': 'unavailable',
                              'errorType': type(diagnostic_error).__name__}), file=output, flush=True)


def warm_models(manager, cache, uid, gid):
    """Copy only regular allowlisted model files from the optional CI download cache."""
    for path in cache.rglob('*'):
        relative = path.relative_to(cache)
        metadata = path.lstat()
        directory = stat.S_ISDIR(metadata.st_mode)
        if (not directory and (not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1)
                or not manager.storage_entry_allowed('subtitles/models/' + relative.as_posix(), directory)):
            raise ValueError('CI model cache contains an unexpected or linked entry')
        target = manager.root / 'subtitles/models' / relative
        if directory:
            target.mkdir(parents=True, exist_ok=True, mode=0o700)
        else:
            target.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
            shutil.copyfile(path, target)
            target.chmod(0o600)
        os.chown(target, uid, gid)


def run_smoke(app_image, model_cache=None):
    if sys.platform != 'linux' or os.geteuid() != 0:
        raise ValueError('Run this smoke gate as root on a disposable Linux Docker runner')
    if not APP_PATTERN.fullmatch(app_image):
        raise ValueError('Use a trusted immutable application digest')
    for name in ('reachcommander', 'reachcommander-subtitles'):
        found = run_command(['docker', 'container', 'ls', '--all', '--filter',
                             'name=^/' + name + '$', '--format', '{{.Names}}'])
        if found:
            raise ValueError('Refusing to touch an existing ' + name + ' container')
    manifest = json.loads(run_command(['docker', 'buildx', 'imagetools', 'inspect', '--raw', app_image], 120))
    previous_image = platform_reference(app_image, manifest, platform.machine())
    run_command(['docker', 'pull', previous_image], 900)
    run_command(['docker', 'pull', app_image], 900)
    uid = gid = 1000
    # Source management requires stable root-owned, non-writable ancestry; /tmp is unsuitable.
    with tempfile.TemporaryDirectory(prefix='reachcommander-subtitle-smoke-', dir='/srv') as temporary:
        workspace = Path(temporary)
        workspace.chmod(0o755)
        root = workspace / ('deployment-' + uuid.uuid4().hex[:12])
        media, archive = workspace / 'media', workspace / 'archive'
        for directory in (media, archive):
            directory.mkdir(mode=0o750)
            os.chown(directory, uid, gid)
        (media / 'preserve.txt').write_text('source contents survive managed lifecycle\n')
        request = renderer.DeploymentRequest.from_mapping({
            'accessMode': 'secure-https', 'bindAddress': '127.0.0.1', 'port': 18092,
            'allowInsecureHttp': False, 'uid': uid, 'gid': gid, 'cpuLimit': '2.0',
            'image': previous_image, 'sources': [{'id': 'media', 'name': 'Media',
                'hostPath': str(media), 'readOnly': False, 'defaultLeft': True, 'defaultRight': True}]})
        renderer.render_deployment(request, REPO / 'deploy/compose.release.yaml', root)
        for relative in ('bin', 'lib', 'backups', 'data', 'updater-socket'):
            (root / relative).mkdir(mode=0o700)
        os.chown(root / 'data', uid, gid)
        for filename in ('render_config.py', 'source_management.py', 'subtitle_management.py'):
            shutil.copyfile(REPO / 'deploy' / filename, root / 'bin' / filename)
            (root / 'bin' / filename).chmod(0o755)
        for filename in ('compose.release.yaml', 'compose.updater.yaml', 'compose.subtitles.yaml', 'updater_protocol.py'):
            text = (REPO / 'deploy' / filename).read_text()
            # Relocate only the host socket mount; this gate does not install a host updater service.
            if filename != 'updater_protocol.py':
                text = text.replace('source: /run/reachcommander-updater',
                                    'source: ' + str(root / 'updater-socket'))
            renderer.atomic_write(root / 'lib' / filename, text, 0o644 if filename.endswith('.py') else 0o600)
        renderer.atomic_write(root / 'compose.override.yaml', (root / 'lib/compose.updater.yaml').read_text())
        manager = SubtitleManager(root)
        stage = 'installed layout validation'
        try:
            SourceTransaction(root)._validate_installer_state()
            stage = 'initial application startup'
            manager.compose('up', '-d', 'reachcommander')
            manager.health('reachcommander')
            stage = 'administrator setup'
            prepare_administrator(root, 'http://127.0.0.1:18092')
            protected = {path.relative_to(root): hashlib.sha256(path.read_bytes()).hexdigest()
                         for path in (root / 'data').rglob('*') if path.is_file()}
            stage = 'worker activation and model setup'
            manager.toggle(True)
            if model_cache:
                warm_models(manager, model_cache.resolve(strict=True), uid, gid)
            manager.download_models()  # Real isolated setup container; a warm cache still validates pinned downloads.
            assert manager.readiness() == (True, True)
            manager.verify()
            status_output = run_command([sys.executable, str(root / 'bin/subtitle_management.py'), 'status'])
            assert 'English models: ready' in status_output and 'Romanian models: ready' in status_output
            print('PASS: real installed manager enable, model setup, identity, health and readiness', flush=True)

            before_invalid = (root / 'compose.override.yaml').read_bytes()
            try:
                manager.preflight('ghcr.io/untrusted/worker:latest')
                raise AssertionError('Untrusted candidate was accepted')
            except ValueError:
                pass
            assert (root / 'compose.override.yaml').read_bytes() == before_invalid

            stage = 'source add and remove'
            sources = SourceTransaction(root)
            added = sources.add(protocol_request('addSource', displayName='Archive', hostPath=str(archive), access='readOnly'))
            assert '/sources/' + added['sourceId'] in (root / 'compose.override.yaml').read_text()
            sources.add(protocol_request('removeSource', sourceId=added['sourceId']))
            assert '/sources/' + added['sourceId'] not in (root / 'compose.override.yaml').read_text()
            assert (media / 'preserve.txt').read_text() == 'source contents survive managed lifecycle\n'
            print('PASS: real source transactions synchronize worker mounts and preserve source data', flush=True)

            # Exercise real deployment publication/health/restore against two immutable refs
            # (platform manifest -> multi-platform manifest), without faking Docker commands.
            stage = 'unhealthy candidate and rollback'
            old_env = (root / '.env').read_text()
            old_overlay = (root / 'compose.override.yaml').read_text()
            template_path = root / 'lib/compose.subtitles.yaml'
            healthy_template = template_path.read_text()
            manager.preflight(app_image)
            renderer.atomic_write(template_path, healthy_template +
                '\n    healthcheck:\n      test: [CMD, python, -c, "raise SystemExit(1)"]\n'
                '      interval: 1s\n      timeout: 1s\n      retries: 1\n      start_period: 0s\n')
            renderer.set_env_image(root / '.env', app_image)
            manager.sync()
            manager.compose('up', '-d', 'reachcommander', 'subtitle-worker')
            failed = False
            try:
                manager.verify()
            except RuntimeError:
                failed = True
            assert failed, 'Deliberately unhealthy candidate unexpectedly passed'
            renderer.atomic_write(root / '.env', old_env)
            renderer.atomic_write(root / 'compose.override.yaml', old_overlay)
            renderer.atomic_write(template_path, healthy_template)
            manager.compose('up', '-d', 'reachcommander', 'subtitle-worker')
            manager.health('reachcommander')
            manager.verify()
            assert manager.request().image == previous_image
            print('PASS: real unhealthy candidate rejected and previous managed deployment restored', flush=True)

            stage = 'retained model and authentication data'
            model = root / 'subtitles/models/whisper-small/model.bin'
            model_hash = hashlib.sha256(model.read_bytes()).hexdigest()
            manager.toggle(False)
            assert model_hash == hashlib.sha256(model.read_bytes()).hexdigest()
            manager.toggle(True)
            manager.verify()
            for relative, digest in protected.items():
                assert hashlib.sha256((root / relative).read_bytes()).hexdigest() == digest, relative
            print('PASS: disable/re-enable retained models and original application data', flush=True)
        except Exception as error:
            failure_diagnostics(stage, error)
            raise RuntimeError('Managed subtitle smoke failed during ' + stage) from None
        finally:
            try:
                manager.compose('down', '--remove-orphans')
            except Exception as error:
                failure_diagnostics('cleanup', error)
                raise RuntimeError('Managed subtitle smoke cleanup failed') from None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app-image', required=True)
    parser.add_argument('--model-cache', type=Path)
    args = parser.parse_args()
    run_smoke(args.app_image, args.model_cache)


if __name__ == '__main__':
    main()
