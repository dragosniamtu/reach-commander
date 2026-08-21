#!/usr/bin/env python3
"""Validate and render ReachCommander deployment configuration."""

from __future__ import annotations

import argparse
import dataclasses
import json
import os
import pathlib
import re


REPOSITORY = "ghcr.io/dragosniamtu/reach-commander"
SOURCE_ID_PATTERN = re.compile(r"^[a-z0-9][a-z0-9_-]{0,63}$")
VERSION_PART = r"(?:0|[1-9][0-9]*)"
PRERELEASE_PART = r"(?:0|[1-9][0-9]*|[A-Za-z-][0-9A-Za-z-]*)"
VERSION_PATTERN = rf"v{VERSION_PART}\.{VERSION_PART}\.{VERSION_PART}(?:-{PRERELEASE_PART}(?:\.{PRERELEASE_PART})*)?"
IMAGE_PATTERN = re.compile(
    rf"^{re.escape(REPOSITORY)}(?::(?:stable|edge|{VERSION_PATTERN})|@sha256:[0-9a-f]{{64}})$"
)
DANGEROUS_ROOTS = ("/", "/proc", "/sys", "/dev", "/run", "/var/run")
REQUEST_KEYS = {"bindAddress", "port", "uid", "gid", "image", "sources"}
SOURCE_KEYS = {
    "id",
    "name",
    "hostPath",
    "readOnly",
    "defaultLeft",
    "defaultRight",
}
ENV_KEYS = (
    "REACHCOMMANDER_BIND_ADDRESS",
    "REACHCOMMANDER_PORT",
    "REACHCOMMANDER_UID",
    "REACHCOMMANDER_GID",
    "REACHCOMMANDER_IMAGE",
)


def _require_exact_keys(mapping: object, expected: set[str], field: str) -> dict:
    if type(mapping) is not dict or set(mapping) != expected:
        raise ValueError(f"{field}: invalid fields")
    return mapping


def _require_integer(value: object, minimum: int, maximum: int, field: str) -> int:
    if type(value) is not int or not minimum <= value <= maximum:
        raise ValueError(f"{field}: invalid integer")
    return value


def _require_boolean(value: object, field: str) -> bool:
    if type(value) is not bool:
        raise ValueError(f"{field}: invalid boolean")
    return value


def _validate_bind_address(value: object) -> str:
    if type(value) is not str:
        raise ValueError("bindAddress: invalid address")
    parts = value.split(".")
    if len(parts) != 4 or any(
        not part.isascii()
        or not part.isdigit()
        or not 0 <= int(part) <= 255
        for part in parts
    ):
        raise ValueError("bindAddress: invalid address")
    return value


def validate_image(value: object) -> str:
    if type(value) is not str or IMAGE_PATTERN.fullmatch(value) is None:
        raise ValueError("image: invalid reference")
    return value


def _normalize_posix_path(value: str) -> str:
    if not value.startswith("/") or "\x00" in value:
        raise ValueError("sources.hostPath: must be absolute")
    parts: list[str] = []
    for part in pathlib.PurePosixPath(value).parts:
        if part in ("/", "", "."):
            continue
        if part == "..":
            if parts:
                parts.pop()
            continue
        if any(ord(character) < 32 for character in part):
            raise ValueError("sources.hostPath: invalid characters")
        parts.append(part)
    return "/" + "/".join(parts)


def canonical_host_path(value: object) -> str:
    if type(value) is not str:
        raise ValueError("sources.hostPath: invalid path")
    normalized = _normalize_posix_path(value)
    resolved = os.path.realpath(value)
    canonical = _normalize_posix_path(resolved) if resolved.startswith("/") else normalized
    if any(
        canonical == root or root != "/" and canonical.startswith(root + "/")
        for root in DANGEROUS_ROOTS
    ):
        raise ValueError("sources.hostPath: dangerous path")
    return canonical


def _validate_name(value: object) -> str:
    if (
        type(value) is not str
        or not 1 <= len(value) <= 100
        or any(ord(character) < 32 or ord(character) == 127 for character in value)
    ):
        raise ValueError("sources.name: invalid value")
    return value


@dataclasses.dataclass(frozen=True)
class SourceRequest:
    id: str
    name: str
    host_path: str
    read_only: bool
    default_left: bool
    default_right: bool

    @classmethod
    def from_mapping(cls, value: object) -> "SourceRequest":
        mapping = _require_exact_keys(value, SOURCE_KEYS, "sources")
        source_id = mapping["id"]
        if type(source_id) is not str or SOURCE_ID_PATTERN.fullmatch(source_id) is None:
            raise ValueError("sources.id: invalid value")
        return cls(
            id=source_id,
            name=_validate_name(mapping["name"]),
            host_path=canonical_host_path(mapping["hostPath"]),
            read_only=_require_boolean(mapping["readOnly"], "sources.readOnly"),
            default_left=_require_boolean(mapping["defaultLeft"], "sources.defaultLeft"),
            default_right=_require_boolean(mapping["defaultRight"], "sources.defaultRight"),
        )

    def to_request_mapping(self) -> dict:
        return {
            "id": self.id,
            "name": self.name,
            "hostPath": self.host_path,
            "readOnly": self.read_only,
            "defaultLeft": self.default_left,
            "defaultRight": self.default_right,
        }


@dataclasses.dataclass(frozen=True)
class DeploymentRequest:
    bind_address: str
    port: int
    uid: int
    gid: int
    image: str
    sources: tuple[SourceRequest, ...]

    @classmethod
    def from_mapping(cls, value: object) -> "DeploymentRequest":
        mapping = _require_exact_keys(value, REQUEST_KEYS, "request")
        common = _validate_common(mapping)
        raw_sources = mapping["sources"]
        if type(raw_sources) is not list or not raw_sources:
            raise ValueError("sources: at least one source is required")
        sources = tuple(SourceRequest.from_mapping(source) for source in raw_sources)
        if len({source.id for source in sources}) != len(sources):
            raise ValueError("sources.id: duplicate value")
        if len({source.host_path for source in sources}) != len(sources):
            raise ValueError("sources.hostPath: duplicate value")
        if sum(source.default_left for source in sources) != 1:
            raise ValueError("defaultLeft: exactly one source is required")
        if sum(source.default_right for source in sources) != 1:
            raise ValueError("defaultRight: exactly one source is required")
        return cls(*common, sources=sources)


def _validate_common(mapping: dict) -> tuple[str, int, int, int, str]:
    return (
        _validate_bind_address(mapping["bindAddress"]),
        _require_integer(mapping["port"], 1, 65535, "port"),
        _require_integer(mapping["uid"], 1, 2147483647, "uid"),
        _require_integer(mapping["gid"], 1, 2147483647, "gid"),
        validate_image(mapping["image"]),
    )


def _load_json(path: pathlib.Path, field: str) -> object:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, UnicodeError) as error:
        raise ValueError(f"{field}: invalid JSON") from error


def load_request(path: pathlib.Path | str) -> DeploymentRequest:
    return DeploymentRequest.from_mapping(_load_json(pathlib.Path(path), "request"))


def yaml_scalar(value: str) -> str:
    if any(ord(character) < 32 or ord(character) == 127 for character in value):
        raise ValueError("YAML scalar: invalid characters")
    return "'" + value.replace("'", "''") + "'"


def _ensure_directory(path: pathlib.Path) -> None:
    path.mkdir(parents=True, exist_ok=True)
    try:
        path.chmod(0o700)
    except OSError:
        if os.name != "nt":
            raise


def atomic_write(path: pathlib.Path | str, content: str, mode: int = 0o600) -> None:
    destination = pathlib.Path(path)
    _ensure_directory(destination.parent)
    temporary: pathlib.Path | None = None
    for counter in range(100):
        candidate = destination.with_name(
            f".{destination.name}.tmp.{os.getpid()}.{counter}"
        )
        try:
            descriptor = os.open(
                candidate,
                os.O_WRONLY | os.O_CREAT | os.O_EXCL,
                mode,
            )
            temporary = candidate
            break
        except FileExistsError:
            continue
    else:
        raise OSError("unable to allocate atomic output")

    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, destination)
        temporary = None
        try:
            directory_descriptor = os.open(destination.parent, os.O_RDONLY)
        except OSError:
            directory_descriptor = None
        if directory_descriptor is not None:
            try:
                os.fsync(directory_descriptor)
            finally:
                os.close(directory_descriptor)
    finally:
        if temporary is not None:
            try:
                temporary.unlink()
            except FileNotFoundError:
                pass


def _json_document(value: object) -> str:
    return json.dumps(value, indent=2, ensure_ascii=False) + "\n"


def render_deployment(
    request: DeploymentRequest,
    template_path: pathlib.Path | str,
    output_path: pathlib.Path | str,
) -> None:
    template = pathlib.Path(template_path).read_text(encoding="utf-8")
    marker = "# installer-source-mounts"
    if template.count(marker) != 1:
        raise ValueError("template: expected one source mount marker")

    mount_blocks: list[str] = []
    for source in request.sources:
        mount_blocks.extend(
            (
                "      - type: bind",
                f"        source: {yaml_scalar(source.host_path)}",
                f"        target: {yaml_scalar('/sources/' + source.id)}",
                f"        read_only: {'true' if source.read_only else 'false'}",
            )
        )
    compose = template.replace("      " + marker, "\n".join(mount_blocks))

    environment = (
        f"REACHCOMMANDER_BIND_ADDRESS={request.bind_address}\n"
        f"REACHCOMMANDER_PORT={request.port}\n"
        f"REACHCOMMANDER_UID={request.uid}\n"
        f"REACHCOMMANDER_GID={request.gid}\n"
        f"REACHCOMMANDER_IMAGE={request.image}\n"
    )
    sources = {
        "sources": [
            {
                "id": source.id,
                "name": source.name,
                "path": f"/sources/{source.id}",
                "enabled": True,
                "readOnly": source.read_only,
                "defaultLeft": source.default_left,
                "defaultRight": source.default_right,
            }
            for source in request.sources
        ]
    }
    source_mounts = {
        "sources": [
            {
                "id": source.id,
                "hostPath": source.host_path,
                "access": "ro" if source.read_only else "rw",
            }
            for source in request.sources
        ]
    }

    output = pathlib.Path(output_path)
    atomic_write(output / ".env", environment)
    atomic_write(output / "compose.yaml", compose)
    atomic_write(output / "config" / "sources.json", _json_document(sources))
    atomic_write(output / "state" / "source-mounts.json", _json_document(source_mounts))


def _raw_request(path: pathlib.Path) -> dict:
    value = _load_json(path, "request")
    return _require_exact_keys(value, REQUEST_KEYS, "request")


def create_request(
    output: pathlib.Path,
    bind_address: str,
    port: int,
    uid: int,
    gid: int,
    image: str,
) -> None:
    mapping = {
        "bindAddress": bind_address,
        "port": port,
        "uid": uid,
        "gid": gid,
        "image": image,
        "sources": [],
    }
    _validate_common(mapping)
    atomic_write(output, _json_document(mapping))


def add_source(
    request_path: pathlib.Path,
    source_id: str,
    name: str,
    host_path: str,
    access: str,
    default_left: bool,
    default_right: bool,
) -> None:
    if access not in ("ro", "rw"):
        raise ValueError("sources.access: invalid value")
    mapping = _raw_request(request_path)
    _validate_common(mapping)
    raw_sources = mapping["sources"]
    if type(raw_sources) is not list:
        raise ValueError("sources: invalid list")
    source = SourceRequest.from_mapping(
        {
            "id": source_id,
            "name": name,
            "hostPath": host_path,
            "readOnly": access == "ro",
            "defaultLeft": default_left,
            "defaultRight": default_right,
        }
    )
    existing = tuple(SourceRequest.from_mapping(item) for item in raw_sources)
    if any(item.id == source.id for item in existing):
        raise ValueError("sources.id: duplicate value")
    if any(item.host_path == source.host_path for item in existing):
        raise ValueError("sources.hostPath: duplicate value")
    mapping["sources"] = [item.to_request_mapping() for item in existing] + [
        source.to_request_mapping()
    ]
    atomic_write(request_path, _json_document(mapping))


def _read_env(path: pathlib.Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        if not line or "=" not in line:
            raise ValueError("env: invalid format")
        key, value = line.split("=", 1)
        if key not in ENV_KEYS or key in values:
            raise ValueError("env: invalid key")
        values[key] = value
    if tuple(values) != ENV_KEYS:
        raise ValueError("env: missing or reordered keys")
    _validate_bind_address(values["REACHCOMMANDER_BIND_ADDRESS"])
    try:
        port = int(values["REACHCOMMANDER_PORT"])
        uid = int(values["REACHCOMMANDER_UID"])
        gid = int(values["REACHCOMMANDER_GID"])
    except ValueError as error:
        raise ValueError("env: invalid integer") from error
    _require_integer(port, 1, 65535, "port")
    _require_integer(uid, 1, 2147483647, "uid")
    _require_integer(gid, 1, 2147483647, "gid")
    validate_image(values["REACHCOMMANDER_IMAGE"])
    return values


def set_env_image(path: pathlib.Path | str, image: str) -> None:
    destination = pathlib.Path(path)
    values = _read_env(destination)
    values["REACHCOMMANDER_IMAGE"] = validate_image(image)
    atomic_write(
        destination,
        "".join(f"{key}={values[key]}\n" for key in ENV_KEYS),
    )


def source_paths(path: pathlib.Path | str) -> tuple[str, ...]:
    mapping = _load_json(pathlib.Path(path), "source mounts")
    if type(mapping) is not dict or set(mapping) != {"sources"}:
        raise ValueError("source mounts: invalid fields")
    items = mapping["sources"]
    if type(items) is not list or not items:
        raise ValueError("source mounts: at least one source is required")
    result: list[str] = []
    ids: set[str] = set()
    for item in items:
        if type(item) is not dict or set(item) != {"id", "hostPath", "access"}:
            raise ValueError("source mounts: invalid source")
        source_id = item["id"]
        if type(source_id) is not str or SOURCE_ID_PATTERN.fullmatch(source_id) is None:
            raise ValueError("source mounts.id: invalid value")
        if source_id in ids:
            raise ValueError("source mounts.id: duplicate value")
        ids.add(source_id)
        if item["access"] not in ("ro", "rw"):
            raise ValueError("source mounts.access: invalid value")
        result.append(canonical_host_path(item["hostPath"]))
    if len(set(result)) != len(result):
        raise ValueError("source mounts.hostPath: duplicate value")
    return tuple(result)


def _parse_boolean(value: str) -> bool:
    if value == "true":
        return True
    if value == "false":
        return False
    raise argparse.ArgumentTypeError("expected true or false")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    create = subparsers.add_parser("create-request")
    create.add_argument("--output", required=True, type=pathlib.Path)
    create.add_argument("--bind-address", required=True)
    create.add_argument("--port", required=True, type=int)
    create.add_argument("--uid", required=True, type=int)
    create.add_argument("--gid", required=True, type=int)
    create.add_argument("--image", required=True)

    add = subparsers.add_parser("add-source")
    add.add_argument("--request", required=True, type=pathlib.Path)
    add.add_argument("--id", required=True)
    add.add_argument("--name", required=True)
    add.add_argument("--host-path", required=True)
    add.add_argument("--access", required=True, choices=("ro", "rw"))
    add.add_argument("--default-left", required=True, type=_parse_boolean)
    add.add_argument("--default-right", required=True, type=_parse_boolean)

    render = subparsers.add_parser("render")
    render.add_argument("--request", required=True, type=pathlib.Path)
    render.add_argument("--template", required=True, type=pathlib.Path)
    render.add_argument("--output", required=True, type=pathlib.Path)

    set_image = subparsers.add_parser("set-image")
    set_image.add_argument("--env", required=True, type=pathlib.Path)
    set_image.add_argument("--image", required=True)

    paths = subparsers.add_parser("source-paths")
    paths.add_argument("--sources", required=True, type=pathlib.Path)
    return parser


def main(arguments: list[str] | None = None) -> int:
    args = build_parser().parse_args(arguments)
    try:
        if args.command == "create-request":
            create_request(
                args.output,
                args.bind_address,
                args.port,
                args.uid,
                args.gid,
                args.image,
            )
        elif args.command == "add-source":
            add_source(
                args.request,
                args.id,
                args.name,
                args.host_path,
                args.access,
                args.default_left,
                args.default_right,
            )
        elif args.command == "render":
            render_deployment(load_request(args.request), args.template, args.output)
        elif args.command == "set-image":
            set_env_image(args.env, args.image)
        elif args.command == "source-paths":
            for path in source_paths(args.sources):
                os.write(1, path.encode("utf-8") + b"\0")
        else:
            raise ValueError("command: unsupported")
    except ValueError as error:
        os.write(2, f"error: {error}\n".encode("utf-8", errors="replace"))
        return 2
    except OSError as error:
        detail = error.strerror or "filesystem operation failed"
        os.write(2, f"error: {detail}\n".encode("utf-8", errors="replace"))
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
