#!/usr/bin/env python3
"""Emit a one-time sanitized ReachCommander support bundle to standard output."""

from __future__ import annotations

import os
import sys
from pathlib import Path
from typing import BinaryIO, Sequence, TextIO

sys.dont_write_bytecode = True

try:
    from .support_bundle import HostDiagnosticCollector, build_support_bundle
except ImportError:  # Installed helper: bin/support_bundle_cli.py + lib/support_bundle.py
    library_directory = Path(__file__).resolve().parents[1] / "lib"
    sys.path.insert(0, str(library_directory))
    from support_bundle import (  # type: ignore[no-redef]
        HostDiagnosticCollector,
        build_support_bundle,
    )


USAGE = "Usage: sudo reachcommander support-bundle > reachcommander-support.zip"
FAILURE = "ReachCommander could not create the sanitized support bundle."


def _paths() -> tuple[Path, str]:
    if (
        os.environ.get("REACHCOMMANDER_TESTING") == "1"
        and (not hasattr(os, "geteuid") or os.geteuid() != 0)
    ):
        root = os.environ.get("REACHCOMMANDER_TEST_INSTALL_ROOT", "")
        command = os.environ.get("REACHCOMMANDER_TEST_COMMAND_PATH", "")
        candidate = Path(root)
        if not root or not candidate.is_absolute() or not command:
            raise ValueError("the fixed support-bundle test paths are unavailable")
        return candidate, command
    return Path("/opt/reachcommander"), "/usr/local/bin/reachcommander"


def main(
    argv: Sequence[str] | None = None,
    *,
    output: BinaryIO | None = None,
    error: TextIO | None = None,
) -> int:
    arguments = tuple(sys.argv[1:] if argv is None else argv)
    stderr = sys.stderr if error is None else error
    stdout = sys.stdout.buffer if output is None else output
    if arguments:
        print(USAGE, file=stderr)
        return 64
    if output is None and sys.stdout.isatty():
        print("Refusing to write ZIP data to an interactive terminal.", file=stderr)
        print(USAGE, file=stderr)
        return 64
    try:
        root, command = _paths()
        bundle = build_support_bundle(
            HostDiagnosticCollector(root, command_path=command).collect()
        )
        stdout.write(bundle)
        stdout.flush()
        print("ReachCommander sanitized support bundle created.", file=stderr)
        return 0
    except (OSError, ValueError, TypeError):
        print(FAILURE, file=stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
