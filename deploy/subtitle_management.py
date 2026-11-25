#!/usr/bin/env python3
"""Root-owned, opt-in local subtitle deployment; no browser supplied image references."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import time

sys.dont_write_bytecode = True

if __package__:
    from . import render_config as renderer
else:
    import render_config as renderer

WORKER_REPOSITORY = 'ghcr.io/dragosniamtu/reach-commander'
WORKER_LABEL = 'io.reachcommander.subtitle-worker-image'
WORKER_PATTERN = re.compile(re.escape(WORKER_REPOSITORY) + r'@sha256:[0-9a-f]{64}')
APP_PATTERN = re.compile(re.escape(renderer.REPOSITORY) + r'@sha256:[0-9a-f]{64}')
MODEL_FILES = {
    'whisper-small': {'config.json', 'model.bin', 'tokenizer.json', 'vocabulary.txt', 'README.md'},
    'marian-en-ro': {'config.json', 'generation_config.json', 'pytorch_model.bin', 'source.spm',
                     'target.spm', 'tokenizer_config.json', 'vocab.json', 'README.md'},
}


def run_command(arguments, timeout=30):
    result = subprocess.run(arguments, check=True, timeout=timeout, text=True,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    return result.stdout.strip()


def safe_file(path: Path):
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
        raise ValueError('Subtitle state contains an unsafe file')
    if os.name == 'posix' and (info.st_uid != os.geteuid() or info.st_mode & 0o022):
        raise ValueError('Subtitle deployment state must be root-owned and protected')


class SubtitleManager:
    def __init__(self, root: Path, *, run=run_command):
        self.root = Path(root)
        self.run = run
        self.flag = self.root / 'state/subtitles-enabled'
        self.marker = self.root / 'state/subtitle-transaction.json'

    def enabled(self):
        if not self.flag.exists() and not self.flag.is_symlink():
            return False  # Earlier verified installer bundles did not have this state.
        safe_file(self.flag)
        value = self.flag.read_text(encoding='utf-8')
        if value not in ('true\n', 'false\n'):
            raise ValueError('Invalid subtitle enable state')
        return value == 'true\n'

    def set_enabled(self, enabled):
        renderer.atomic_write(self.flag, 'true\n' if enabled else 'false\n')

    def request(self):
        return renderer.load_installed_request(self.root / '.env', self.root / 'config/sources.json',
                                               self.root / 'state/source-mounts.json')

    def require_idle(self):
        if self.marker.exists() or self.marker.is_symlink():
            raise ValueError('Incomplete subtitle transaction; run reachcommander subtitles recover')

    def worker_image(self, app_image):
        if not APP_PATTERN.fullmatch(app_image):
            raise ValueError('Subtitle application image must be a trusted immutable digest')
        value = self.run(['docker', 'image', 'inspect', '--format',
                          '{{ index .Config.Labels "' + WORKER_LABEL + '" }}', app_image], 30).strip()
        if not WORKER_PATTERN.fullmatch(value):
            raise ValueError('This app image has no valid pinned subtitle worker. Disable subtitles before using older releases.')
        return value

    def render(self, request, worker_image):
        if not WORKER_PATTERN.fullmatch(worker_image):
            raise ValueError('Invalid subtitle worker digest')
        template = self.root / 'lib/compose.subtitles.yaml'
        safe_file(template)
        text = template.read_text(encoding='utf-8')
        mounts = []
        for source in request.sources:
            mounts.extend(('      - type: bind',
                           '        source: ' + renderer.yaml_scalar(source.host_path),
                           '        target: ' + renderer.yaml_scalar('/sources/' + source.id),
                           '        read_only: true', '        bind:', '          create_host_path: false'))
        for marker, replacement in {
            '@WORKER_IMAGE@': worker_image,
            '@MEDIA_ROOTS@': json.dumps(['/sources/' + source.id for source in request.sources]),
            '      # subtitle-source-mounts': '\n'.join(mounts),
        }.items():
            if text.count(marker) != 1:
                raise ValueError('Invalid installed subtitle template')
            text = text.replace(marker, replacement)
        return text

    def preflight(self, app_image):
        self.require_idle()
        if self.enabled():
            image = self.worker_image(app_image)
            self.validate_storage()
            self.render(self.request(), image)
            self.run(['docker', 'pull', image], 900)

    def sync(self, *, output=None, request=None):
        self.require_idle()
        if not self.enabled():
            return
        request = request or self.request()
        text = self.render(request, self.worker_image(request.image))
        renderer.atomic_write((output or self.root) / 'compose.override.yaml', text)

    def storage_entry_allowed(self, relative, is_directory):
        if is_directory:
            if relative in ('subtitles', 'subtitles/mailbox', 'subtitles/mailbox/requests',
                            'subtitles/mailbox/results', 'subtitles/models'):
                return True
            return any(relative == 'subtitles/models/' + model + suffix for model in MODEL_FILES
                       for suffix in ('', '/.cache', '/.cache/huggingface', '/.cache/huggingface/download'))
        if relative in ('subtitles/mailbox/worker.lock', 'subtitles/mailbox/status.json'):
            return True
        if re.fullmatch(r'subtitles/mailbox/\.status\.json\.[a-f0-9]{32}\.tmp', relative):
            return True
        if re.fullmatch(r'subtitles/mailbox/(requests|results)/[a-f0-9]{32}\.(json|cancel)', relative):
            return True
        if re.fullmatch(r'subtitles/mailbox/(requests|results)/\.?[a-f0-9]{32}\.(json|cancel)\.[a-f0-9]{32}\.tmp', relative):
            return True
        if re.fullmatch(r'subtitles/mailbox/(requests|results)/[a-f0-9]{32}\.(json|cancel)\.tmp-[a-f0-9]{32}', relative):
            return True
        for model, names in MODEL_FILES.items():
            prefix = 'subtitles/models/' + model + '/'
            if not relative.startswith(prefix):
                continue
            name = relative[len(prefix):]
            if name in names | {'download-manifest.json', 'download-manifest.json.tmp', '.cache/huggingface/.gitignore'}:
                return True
            for model_file in names:
                if re.fullmatch(r'\.cache/huggingface/download/' + re.escape(model_file) +
                                r'(\.(metadata|lock)|\.[a-f0-9]{40,64}\.incomplete)', name):
                    return True
        return False

    def validate_storage(self, *, request=None):
        storage = self.root / 'subtitles'
        if not storage.exists() and not storage.is_symlink():
            return
        if storage.is_symlink() or not storage.is_dir():
            raise ValueError('Subtitle storage root must be a real directory')
        request = request or self.request()
        for path in [storage, *storage.rglob('*')]:
            try:
                info = path.lstat()
            except FileNotFoundError:
                # Atomic heartbeat/result writers can remove a just-enumerated temporary.
                continue
            relative = path.relative_to(self.root).as_posix()
            directory = stat.S_ISDIR(info.st_mode)
            if (not directory and (not stat.S_ISREG(info.st_mode) or info.st_nlink != 1)
                    or not self.storage_entry_allowed(relative, directory)
                    or os.path.ismount(path)):
                raise ValueError('Subtitle storage contains an unexpected, linked, or mounted entry')
            # Never repair/chown an arbitrary runtime-written subtree as root.
            expected_owner = os.geteuid() if relative == 'subtitles' and os.name == 'posix' else request.uid
            if os.name == 'posix' and (info.st_uid != expected_owner or info.st_mode & 0o022):
                raise ValueError('Subtitle storage owner/permissions differ; retain the original runtime UID/GID')
            if os.name == 'posix' and relative != 'subtitles' and info.st_gid != request.gid:
                raise ValueError('Subtitle storage group differs; retain the original runtime UID/GID')

    def prepare_storage(self):
        self.validate_storage()
        request = self.request()
        for name in ('subtitles', 'subtitles/models', 'subtitles/mailbox',
                     'subtitles/mailbox/requests', 'subtitles/mailbox/results'):
            path = self.root / name
            if not path.exists():
                path.mkdir(mode=0o700)
                path.chmod(0o700)
                if os.name == 'posix' and name != 'subtitles' and os.geteuid() == 0:
                    os.chown(path, request.uid, request.gid)

    def compose(self, *arguments):
        return self.run(['docker', 'compose', '--project-directory', str(self.root), *arguments], 120)

    def stop_worker(self, *, remove=False):
        # Recovery must not depend on which half of flag/overlay publication survived.
        name = self.run(['docker', 'container', 'ls', '--all', '--filter',
                         'name=^/reachcommander-subtitles$', '--format', '{{.Names}}'], 30).strip()
        if name == 'reachcommander-subtitles':
            self.run(['docker', 'stop', '--time', '20', 'reachcommander-subtitles'], 45)
            if remove:
                self.run(['docker', 'rm', 'reachcommander-subtitles'], 30)
        elif name:
            raise ValueError('Unexpected subtitle container identity')

    def health(self, container):
        for attempt in range(61):
            value = self.run(['docker', 'inspect', '--format',
                              '{{if .State.Health}}{{.State.Health.Status}}{{else}}missing{{end}}', container], 15).strip()
            if value == 'healthy':
                return
            if value not in ('starting', 'created', 'restarting'):
                break
            if attempt < 60:
                time.sleep(1)
        raise RuntimeError('Subtitle deployment container failed its health check')

    def verify(self):
        if self.enabled():
            expected = self.run(['docker', 'image', 'inspect', '--format', '{{.Id}}',
                                 self.worker_image(self.request().image)], 15).strip()
            running = self.run(['docker', 'inspect', '--format', '{{.Image}}', 'reachcommander-subtitles'], 15).strip()
            if not re.fullmatch(r'sha256:[a-f0-9]{64}', expected) or expected != running:
                raise RuntimeError('Running subtitle worker does not match selected application')
            self.health('reachcommander-subtitles')

    def readiness(self):
        try:
            path = self.root / 'subtitles/mailbox/status.json'
            descriptor = os.open(path, os.O_RDONLY | getattr(os, 'O_NOFOLLOW', 0) | getattr(os, 'O_NONBLOCK', 0))
            with os.fdopen(descriptor, 'r', encoding='utf-8') as stream:
                if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
                    return False, False
                raw = stream.read(4097)
            if len(raw) > 4096:
                return False, False
            value = json.loads(raw)
            if (type(value) is not dict or set(value) != {'version', 'updatedAt', 'englishAvailable', 'romanianAvailable'}
                    or type(value['version']) is not int or value['version'] != 1
                    or type(value['englishAvailable']) is not bool or type(value['romanianAvailable']) is not bool
                    or value['romanianAvailable'] and not value['englishAvailable']):
                return False, False
            timestamp = datetime.fromisoformat(value['updatedAt'].replace('Z', '+00:00'))
            if timestamp.tzinfo is None or not -5 <= (datetime.now(timezone.utc) - timestamp).total_seconds() <= 30:
                return False, False
            return value['englishAvailable'], value['romanianAvailable']
        except (OSError, ValueError, TypeError, AttributeError):
            return False, False

    def toggle(self, enabled):
        self.require_idle()
        for path in (self.root / 'state/install-transaction', self.root / 'state/update-transaction',
                     self.root / 'backups/.source-transaction'):
            if path.exists() or path.is_symlink():
                raise ValueError('Another deployment transaction requires recovery')
        previous = self.enabled()
        self.prepare_storage()
        overlay = self.root / 'compose.override.yaml'
        safe_file(overlay)
        old_text = overlay.read_text(encoding='utf-8')
        recovery = json.dumps({'enabled': previous, 'overlay': old_text}, ensure_ascii=False) + '\n'
        if len(recovery.encode('utf-8')) > 131072:
            raise ValueError('Subtitle recovery state exceeds the supported size')
        if enabled:
            image = self.worker_image(self.request().image)
            self.run(['docker', 'pull', image], 900)
            text = self.render(self.request(), image)
        else:
            template = self.root / 'lib/compose.updater.yaml'
            safe_file(template)
            text = template.read_text(encoding='utf-8')
        renderer.atomic_write(self.marker, recovery)
        try:
            if previous:
                self.compose('stop', 'subtitle-worker')
                if not enabled:
                    self.compose('rm', '--force', 'subtitle-worker')
            self.set_enabled(enabled)
            renderer.atomic_write(overlay, text)
            self.compose('config', '--quiet')
            self.compose('up', '-d', 'reachcommander', *(['subtitle-worker'] if enabled else []))
            self.health('reachcommander')
            self.verify()
            self.marker.unlink()
        except Exception:
            self.recover()
            raise

    def recover(self):
        safe_file(self.marker)
        if self.marker.stat().st_size > 131072:
            raise ValueError('Invalid subtitle recovery state')
        value = json.loads(self.marker.read_text(encoding='utf-8'))
        if (type(value) is not dict or set(value) != {'enabled', 'overlay'}
                or type(value['enabled']) is not bool or type(value['overlay']) is not str):
            raise ValueError('Invalid subtitle recovery state')
        self.stop_worker(remove=not value['enabled'])
        renderer.atomic_write(self.root / 'compose.override.yaml', value['overlay'])
        self.set_enabled(value['enabled'])
        self.compose('up', '-d', 'reachcommander', *(['subtitle-worker'] if value['enabled'] else []))
        self.health('reachcommander')
        self.verify()
        self.marker.unlink()

    def download_models(self):
        self.require_idle()
        if not self.enabled():
            raise ValueError('Enable subtitles before downloading models')
        self.prepare_storage()
        request = self.request()
        image = self.worker_image(request.image)
        # A separate, disposable container gets network access and only the model directory.
        self.compose('stop', 'subtitle-worker')
        try:
            self.run(['docker', 'run', '--rm', '--user', f'{request.uid}:{request.gid}',
                  '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true',
                  '--cpus', '2.0', '--memory', '2g', '--pids-limit', '128',
                  '--tmpfs', '/tmp:size=256m,mode=1777',
                  '--mount', 'type=bind,src=' + str(self.root / 'subtitles/models') + ',dst=/models',
                  '--env', 'HF_HUB_OFFLINE=0', '--env', 'TRANSFORMERS_OFFLINE=0',
                      '--entrypoint', 'python', image, '/app/download_models.py', '--romanian'], 7200)
        finally:
            self.compose('restart', 'subtitle-worker')
        self.verify()
        for attempt in range(121):
            if self.readiness() == (True, True):
                return
            if attempt < 120:
                time.sleep(1)
        raise RuntimeError('Downloaded models are not ready; inspect reachcommander subtitles status')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('enable', 'disable', 'status', 'download-models', 'recover',
                                          'preflight', 'sync', 'prepare', 'verify', 'storage-check', 'enabled'))
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument('--image')
    parser.add_argument('--config-root', type=Path)
    args = parser.parse_args()
    manager = SubtitleManager(args.root)
    try:
        if args.action in ('enable', 'disable'):
            manager.toggle(args.action == 'enable')
        elif args.action == 'status':
            manager.require_idle()
            manager.validate_storage()
            print('Subtitles: ' + ('enabled (small/int8, CPU only, 2 threads, 6GB)' if manager.enabled() else 'disabled'))
            if manager.enabled():
                manager.verify()
                english, romanian = manager.readiness()
                print('English models: ' + ('ready' if english else 'unavailable'))
                print('Romanian models: ' + ('ready' if romanian else 'unavailable'))
                if not (english and romanian):
                    print('Prepare models explicitly: reachcommander subtitles download-models')
        elif args.action == 'download-models':
            manager.download_models()
        elif args.action == 'recover':
            manager.recover()
        elif args.action == 'preflight':
            manager.preflight(args.image or manager.request().image)
        elif args.action == 'sync':
            manager.sync()
        elif args.action == 'prepare':
            manager.validate_storage()
            if manager.enabled():
                manager.prepare_storage()
        elif args.action == 'verify':
            manager.verify()
        elif args.action == 'storage-check':
            manager.validate_storage(request=SubtitleManager(args.config_root).request() if args.config_root else None)
        elif args.action == 'enabled':
            print('true' if manager.enabled() else 'false')
        return 0
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        # Docker stderr may contain host details; keep that in root-side Docker logs.
        print('ReachCommander subtitles: ' + (str(error) if isinstance(error, (ValueError, RuntimeError))
                                              else 'operation failed; inspect Docker and run reachcommander doctor'),
              file=sys.stderr)
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
