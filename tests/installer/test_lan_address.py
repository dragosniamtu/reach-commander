from __future__ import annotations

import contextlib
import importlib.util
import io
import sys
import unittest
from pathlib import Path
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
MODULE_PATH = ROOT / "deploy" / "lan_address.py"


def import_lan_address():
    if not MODULE_PATH.is_file():
        return None
    spec = importlib.util.spec_from_file_location(
        "reachcommander_lan_address", MODULE_PATH
    )
    if spec is None or spec.loader is None:
        return None
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


class LanAddressTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self.module = import_lan_address()
        self.assertIsNotNone(self.module, "deploy/lan_address.py must exist")

    def candidate(self, interface: str, address: str) -> dict:
        return {
            "ifname": interface,
            "flags": ["BROADCAST", "MULTICAST", "UP", "LOWER_UP"],
            "addr_info": [
                {
                    "family": "inet",
                    "local": address,
                    "prefixlen": 24,
                    "scope": "global",
                }
            ],
        }

    def test_prefers_default_route_physical_ethernet(self) -> None:
        addresses = [
            self.candidate("enp3s0", "192.168.50.20"),
            self.candidate("wlp2s0", "10.0.0.8"),
        ]
        routes = [{"dst": "default", "dev": "enp3s0", "metric": 100}]
        self.assertEqual(
            ("192.168.50.20",),
            self.module.discover_display_addresses(
                addresses, routes, {"enp3s0", "wlp2s0"}
            ),
        )

    def test_prefers_default_route_wifi_without_hardcoded_eth0(self) -> None:
        addresses = [self.candidate("wlan42", "10.20.30.40")]
        routes = [{"dst": "default", "dev": "wlan42", "metric": 600}]
        self.assertEqual(
            ("10.20.30.40",),
            self.module.discover_display_addresses(addresses, routes, {"wlan42"}),
        )

    def test_accepts_all_rfc1918_ranges(self) -> None:
        addresses = [
            self.candidate("lan10", "10.1.2.3"),
            self.candidate("lan172", "172.31.4.5"),
            self.candidate("lan192", "192.168.6.7"),
        ]
        self.assertEqual(
            ("10.1.2.3", "172.31.4.5", "192.168.6.7"),
            self.module.discover_display_addresses(
                addresses, [], {"lan10", "lan172", "lan192"}
            ),
        )

    def test_filters_docker_loopback_link_local_public_and_cgnat(self) -> None:
        addresses = [
            self.candidate("docker0", "172.17.0.1"),
            self.candidate("br-deadbeef", "172.18.0.1"),
            self.candidate("lo", "127.0.0.1"),
            self.candidate("enp3s0", "169.254.10.1"),
            self.candidate("enp3s0", "203.0.113.10"),
            self.candidate("enp3s0", "100.64.10.1"),
        ]
        self.assertEqual(
            (),
            self.module.discover_display_addresses(addresses, [], {"enp3s0"}),
        )

    def test_physical_candidate_wins_over_other_virtual_interface(self) -> None:
        addresses = [
            self.candidate("enp3s0", "192.168.1.20"),
            self.candidate("wg-home", "10.8.0.2"),
        ]
        self.assertEqual(
            ("192.168.1.20",),
            self.module.discover_display_addresses(addresses, [], {"enp3s0"}),
        )

    def test_multiple_equal_candidates_are_deterministic(self) -> None:
        addresses = [
            self.candidate("lan-b", "192.168.2.20"),
            self.candidate("lan-a", "192.168.1.20"),
        ]
        self.assertEqual(
            ("192.168.1.20", "192.168.2.20"),
            self.module.discover_display_addresses(
                addresses, [], {"lan-a", "lan-b"}
            ),
        )

    def test_unavailable_system_discovery_is_a_non_fatal_empty_result(self) -> None:
        output = io.StringIO()
        with mock.patch.object(
            self.module,
            "system_snapshot",
            return_value=([], [], set()),
        ), contextlib.redirect_stdout(output):
            status = self.module.main()

        self.assertEqual(0, status)
        self.assertEqual("", output.getvalue())


if __name__ == "__main__":
    unittest.main()
