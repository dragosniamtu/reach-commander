from __future__ import annotations

import subprocess
import sys
from typing import Sequence, TextIO


MAX_ANNOTATION_LENGTH = 3_500


def _escape_message(value: str) -> str:
    return value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def _escape_property(value: str) -> str:
    return _escape_message(value).replace(":", "%3A").replace(",", "%2C")


def _bounded_output(value: str) -> str:
    normalized = value.strip() or "The command failed without diagnostic output."
    if len(normalized) <= MAX_ANNOTATION_LENGTH:
        return normalized
    separator = "\n...\n"
    available = MAX_ANNOTATION_LENGTH - len(separator)
    leading = available // 2
    trailing = available - leading
    return f"{normalized[:leading]}{separator}{normalized[-trailing:]}"


def run_command(command: Sequence[str], title: str, output: TextIO) -> int:
    completed = subprocess.run(
        command,
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    output.write(completed.stdout)
    if completed.returncode != 0:
        print(
            f"::error title={_escape_property(title)}::"
            f"{_escape_message(_bounded_output(completed.stdout))}",
            file=output,
        )
    return completed.returncode


def main(arguments: Sequence[str]) -> int:
    if len(arguments) < 2:
        print(
            "::error title=CI diagnostic wrapper failed::"
            "Expected a title followed by a command."
        )
        return 2
    return run_command(arguments[1:], arguments[0], sys.stdout)


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
