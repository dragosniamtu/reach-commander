"""Offline validation of pinned model downloads; no ML imports or network calls."""
import hashlib
import json
from pathlib import Path
import re
import stat

from download_models import MODELS

MAX_MANIFEST = 64 * 1024


def safe_file(path):
    for entry in (path, *path.parents):
        metadata = entry.lstat()
        if stat.S_ISLNK(metadata.st_mode) or getattr(metadata, "st_file_attributes", 0) & 0x400:
            raise ValueError("Linked model artifact")
    metadata = path.stat()
    if not stat.S_ISREG(metadata.st_mode) or metadata.st_size <= 0:
        raise ValueError("Missing model artifact")
    return metadata


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate manifest field")
        result[key] = value
    return result


class ModelReadiness:
    """Rehash only when artifact identity, size or timestamps change.

    The optional callback revokes previously published readiness before a slow
    validation starts. Call snapshot from one thread; heartbeat publication stays
    on the supervisor thread while model files are being scanned.
    """
    def __init__(self, models: Path, invalidated=None):
        self.models = models
        self.invalidated = invalidated or (lambda: None)
        self.cache = {}

    def valid(self, name):
        model = MODELS[name]
        target = self.models / name
        signature = None
        try:
            paths = [target / "download-manifest.json", *(target / name for name in model["files"])]
            signature = tuple((info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns)
                              for info in map(safe_file, paths))
            if signature[0][2] > MAX_MANIFEST:
                raise ValueError("Oversized manifest")
            previous = self.cache.get(name)
            if previous is not None and previous[0] == signature:
                return previous[1]
            self.invalidated()
            with paths[0].open("rb") as source:
                data = source.read(MAX_MANIFEST + 1)
            if len(data) > MAX_MANIFEST:
                raise ValueError("Oversized manifest")
            manifest = json.loads(data, object_pairs_hook=unique_object)
            if not isinstance(manifest, dict) or any(manifest.get(key) != value for key, value in model.items()):
                raise ValueError("Unpinned model manifest")
            hashes = manifest.get("sha256")
            if not isinstance(hashes, dict) or set(hashes) != set(model["files"]):
                raise ValueError("Missing model hashes")
            for path in paths[1:]:
                digest = hashes[path.name]
                if not isinstance(digest, str) or re.fullmatch(r"[a-f0-9]{64}", digest) is None:
                    raise ValueError("Invalid model hash")
                with path.open("rb") as source:
                    if hashlib.file_digest(source, "sha256").hexdigest() != digest:
                        raise ValueError("Corrupt model artifact")
            # A concurrent setup replacement is never published as ready.
            after = tuple((info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns)
                          for info in map(safe_file, paths))
            if after != signature:
                raise ValueError("Model changed during validation")
            self.cache[name] = (signature, True)
            return True
        except (OSError, ValueError, TypeError, UnicodeError, RecursionError):
            if signature is not None:
                self.cache[name] = (signature, False)
            else:
                self.cache.pop(name, None)
            return False

    def snapshot(self):
        english = self.valid("whisper-small")
        return english, english and self.valid("marian-en-ro")


def validate_models(models: Path) -> tuple[bool, bool]:
    """Return (English available, Romanian available), verifying hashes afresh."""
    return ModelReadiness(models).snapshot()
