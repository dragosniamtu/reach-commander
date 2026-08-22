from __future__ import annotations

from glob import glob
from pathlib import Path
import sys
from typing import Sequence, TextIO
import xml.etree.ElementTree as ET


MAX_FAILURE_MESSAGE_LENGTH = 3_500


def expand_paths(arguments: Sequence[str]) -> list[Path]:
    paths: list[Path] = []
    for argument in arguments:
        matches = [Path(match) for match in sorted(glob(argument))]
        paths.extend(matches or [Path(argument)])
    return paths


def _escape_message(value: str) -> str:
    return value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")


def _escape_property(value: str) -> str:
    return _escape_message(value).replace(":", "%3A").replace(",", "%2C")


def _bounded_message(value: str) -> str:
    normalized = value.strip() or "No failure message was recorded."
    if len(normalized) <= MAX_FAILURE_MESSAGE_LENGTH:
        return normalized
    return f"{normalized[: MAX_FAILURE_MESSAGE_LENGTH - 3]}..."


def report(paths: Sequence[Path], output: TextIO) -> None:
    for path in paths:
        try:
            root = ET.parse(path).getroot()
        except FileNotFoundError:
            print(
                "::warning title=.NET diagnostics unavailable::"
                f"TRX file was not found: {_escape_message(path.name)}",
                file=output,
            )
            continue
        except (ET.ParseError, OSError):
            print(
                "::warning title=.NET diagnostics unavailable::"
                f"Could not parse TRX file: {_escape_message(path.name)}",
                file=output,
            )
            continue

        failed_results = [
            result
            for result in root.findall(".//{*}UnitTestResult")
            if result.get("outcome", "").casefold() == "failed"
        ]
        if not failed_results:
            print(f"No failed .NET test details were found in {path.name}.", file=output)
            continue

        for result in failed_results:
            test_name = result.get("testName") or "Unknown .NET test"
            message_element = result.find(".//{*}ErrorInfo/{*}Message")
            message = _bounded_message(
                message_element.text if message_element is not None and message_element.text else ""
            )
            print(
                f"::error title={_escape_property(f'.NET test failed - {test_name}')}::"
                f"{_escape_message(message)}",
                file=output,
            )


def main(arguments: Sequence[str]) -> int:
    if not arguments:
        print(
            "::warning title=.NET diagnostics unavailable::No TRX files were provided."
        )
        return 0

    report(expand_paths(arguments), sys.stdout)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
