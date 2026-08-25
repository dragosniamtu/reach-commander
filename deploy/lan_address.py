#!/usr/bin/env python3
"""Print useful private-LAN addresses for installer completion output."""

from __future__ import annotations

import ipaddress
import json
import os
import pathlib
import subprocess
from collections.abc import Collection
from dataclasses import dataclass


RFC1918 = (
    ipaddress.ip_network("10.0.0.0/8"),
    ipaddress.ip_network("172.16.0.0/12"),
    ipaddress.ip_network("192.168.0.0/16"),
)
DOCKER_PREFIXES = ("docker", "br-", "veth", "virbr", "cni", "flannel")


@dataclass(frozen=True)
class Candidate:
    interface: str
    address: ipaddress.IPv4Address
    default_metric: int | None
    physical: bool


def _is_rfc1918(address: ipaddress.IPv4Address) -> bool:
    return any(address in network for network in RFC1918)


def _default_metrics(routes: object) -> dict[str, int]:
    result: dict[str, int] = {}
    if type(routes) is not list:
        return result
    for route in routes:
        if type(route) is not dict or route.get("dst") != "default":
            continue
        interface = route.get("dev")
        metric = route.get("metric", 0)
        if type(interface) is not str or type(metric) is not int:
            continue
        result[interface] = min(result.get(interface, metric), metric)
    return result


def discover_display_addresses(
    addresses: object,
    routes: object,
    physical_interfaces: Collection[str],
) -> tuple[str, ...]:
    if type(addresses) is not list:
        return ()
    defaults = _default_metrics(routes)
    candidates: list[Candidate] = []
    for interface_data in addresses:
        if type(interface_data) is not dict:
            continue
        interface = interface_data.get("ifname")
        flags = interface_data.get("flags", [])
        if type(interface) is not str or type(flags) is not list or "UP" not in flags:
            continue
        if interface == "lo" or interface.startswith(DOCKER_PREFIXES):
            continue
        address_info = interface_data.get("addr_info", [])
        if type(address_info) is not list:
            continue
        for item in address_info:
            if (
                type(item) is not dict
                or item.get("family") != "inet"
                or item.get("scope") != "global"
            ):
                continue
            try:
                address = ipaddress.IPv4Address(item.get("local"))
            except ipaddress.AddressValueError:
                continue
            if not _is_rfc1918(address):
                continue
            candidates.append(
                Candidate(
                    interface=interface,
                    address=address,
                    default_metric=defaults.get(interface),
                    physical=interface in physical_interfaces,
                )
            )
    if any(candidate.physical for candidate in candidates):
        candidates = [candidate for candidate in candidates if candidate.physical]
    routed = [candidate for candidate in candidates if candidate.default_metric is not None]
    if routed:
        best_metric = min(candidate.default_metric for candidate in routed)
        candidates = [
            candidate for candidate in routed if candidate.default_metric == best_metric
        ]
    ordered = sorted(candidates, key=lambda item: (item.interface, int(item.address)))
    return tuple(dict.fromkeys(str(candidate.address) for candidate in ordered))


def _ip_json(*arguments: str) -> object:
    try:
        result = subprocess.run(
            ["ip", "-j", "-4", *arguments],
            check=True,
            capture_output=True,
            text=True,
            timeout=5,
        )
        return json.loads(result.stdout)
    except (OSError, subprocess.SubprocessError, json.JSONDecodeError):
        return []


def _physical_interfaces(root: pathlib.Path = pathlib.Path("/sys/class/net")) -> set[str]:
    try:
        return {entry.name for entry in root.iterdir() if (entry / "device").exists()}
    except OSError:
        return set()


def system_snapshot() -> tuple[object, object, set[str]]:
    if os.environ.get("REACHCOMMANDER_TESTING") == "1":
        try:
            return (
                json.loads(os.environ.get("FAKE_IP_ADDRESS_JSON", "[]")),
                json.loads(os.environ.get("FAKE_IP_ROUTE_JSON", "[]")),
                set(),
            )
        except json.JSONDecodeError:
            return ([], [], set())
    return (
        _ip_json("address", "show", "up"),
        _ip_json("route", "show", "default"),
        _physical_interfaces(),
    )


def main() -> int:
    addresses, routes, physical_interfaces = system_snapshot()
    for address in discover_display_addresses(addresses, routes, physical_interfaces):
        print(address)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
