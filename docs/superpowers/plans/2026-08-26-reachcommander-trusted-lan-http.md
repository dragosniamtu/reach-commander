# ReachCommander Radarr-Style Trusted LAN HTTP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an explicit Ubuntu installer mode that publishes ReachCommander on every host interface at host port `8092`, forwards to container port `8080`, and keeps Production authentication, antiforgery, authorization, and rate limiting enabled over trusted-LAN HTTP.

**Architecture:** The installer records a validated access-mode policy in the generated request and `.env`; Compose publishes either loopback or wildcard host networking and passes the policy to ASP.NET Core. A small standard-library Python helper inspects local `ip -j` data only to print useful RFC1918 URLs, while the backend changes only cookie transport policy when the explicit LAN setting is true.

**Tech Stack:** Bash, Python 3 standard library, Docker Compose v2, ASP.NET Core/.NET 10, xUnit integration tests, Node.js contract tests, GitHub Actions

## Global Constraints

- Work directly on `master`; do not create a Git worktree.
- Secure HTTPS reverse-proxy mode remains the recommended installer default.
- Secure mode publishes `127.0.0.1:8092:8080` by default.
- Trusted LAN HTTP publishes wildcard host port `8092` to container port `8080`.
- The host port remains configurable; the container port remains exactly `8080`.
- Trusted LAN HTTP must require explicit installer selection and acknowledgement.
- `ASPNETCORE_ENVIRONMENT` remains Production in deployed containers.
- Administrator authentication, authorization, automatic antiforgery validation, and authentication rate limiting remain enabled.
- `Authentication:AllowInsecureHttp` defaults to `false` when absent.
- LAN address discovery is display-only, uses no Internet service, and never becomes a bind or authorization boundary.
- DHCP changes require no ReachCommander reconfiguration in wildcard LAN mode.
- The installer does not alter Ubuntu firewall rules, router forwarding, DNS, certificates, or reverse-proxy configuration.
- Reconfiguration preserves `/data`, account state, Data Protection keys, sources, durable operations, and update state.
- Use only Python's standard library; add no runtime or installer dependency.
- Do not modify or stage the unrelated untracked `NC-theme.png`.

---

## File structure

### Create

- `deploy/lan_address.py` — pure candidate selection plus a non-fatal CLI adapter over local `ip -j` and `/sys/class/net` data.
- `tests/installer/test_lan_address.py` — deterministic address/routing fixtures covering physical, Wi-Fi, RFC1918, Docker, virtual, and fallback cases.

### Modify

- `src/ReachCommander.Api/Authentication/AuthenticationConfiguration.cs` — configuration-driven cookie transport policy only.
- `tests/ReachCommander.IntegrationTests/AuthenticationApiTests.cs` — Production secure-default and trusted-LAN HTTP authentication/antiforgery coverage.
- `deploy/render_config.py` — access-mode schema, policy validation, generated environment keys, and image-update parsing.
- `deploy/compose.release.yaml` — pass `Authentication__AllowInsecureHttp` to the backend.
- `tests/installer/fixtures/valid-request.json` — secure-mode request fixture.
- `tests/installer/test_render_config.py` — secure/LAN rendering and contradictory-policy rejection.
- `deploy/package-installer.sh` — include the LAN display helper in the deterministic release allowlist.
- `tests/installer/test_package.sh` — assert helper presence, mode, and deterministic packaging.
- `.github/workflows/ci.yml` — run LAN discovery tests and validate both generated networking modes in hardened smoke.
- `tests/installer/workflow-contract.test.mjs` — keep the new CI gate mandatory.
- `deploy/install.sh` — explicit access-mode prompt, warning acknowledgement, renderer argument, and mode-specific completion output.
- `tests/installer/test_install.sh` — secure default, LAN opt-in, output, reconfiguration preservation, and rollback contracts.
- `tests/installer/fake-bin/ip` — deterministic `ip -j` responses for Bash installer contracts.
- `deploy/reachcommander` — doctor validation and explicit wildcard warning.
- `tests/installer/test_command.sh` — doctor success/failure contracts for consistent and contradictory policies.
- `docs/deployment/ubuntu.md` — operator flow, security boundary, DHCP behavior, ports, and PWA limitation.
- `README.md` — advertise optional trusted-LAN access without weakening the default recommendation.
- `docs/INSTALL.md` — summarize both Ubuntu network modes.
- `deploy/README.md` — document the bundle helper and access-mode contract.
- `SECURITY.md` — document wildcard HTTP as an explicit trusted-network exception.
- `tests/installer/docs-contract.test.mjs` — enforce the published security and port-language contract.

---

### Task 1: Configuration-driven Production cookie transport

**Files:**
- Modify: `tests/ReachCommander.IntegrationTests/AuthenticationApiTests.cs`
- Modify: `src/ReachCommander.Api/Authentication/AuthenticationConfiguration.cs`

**Interfaces:**
- Consumes: `IConfiguration`, `IHostEnvironment`, existing `Authentication:DataPath`, cookie authentication, and ASP.NET Core antiforgery registration.
- Produces: private `CookieSecurePolicy CookieSecurePolicy(IConfiguration configuration, IHostEnvironment environment)` used identically by session and antiforgery cookies.

- [ ] **Step 1: Write failing Production HTTP policy tests**

Add these tests after `Antiforgery_bootstrap_uses_an_httponly_strict_cookie` in `AuthenticationApiTests.cs`:

```csharp
    [Fact]
    public async Task Production_http_keeps_secure_cookies_by_default()
    {
        await using var factory = new ReachCommanderApiFactory(
            useRealSecurity: true,
            environmentName: "Production");
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_trusted_lan_http_supports_setup_without_disabling_security()
    {
        await using var factory = new ReachCommanderApiFactory(
            useRealSecurity: true,
            environmentName: "Production",
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Authentication:AllowInsecureHttp"] = "true",
            });
        using var client = CreateClient(factory);

        var antiforgery = await client.GetAsync("/api/auth/antiforgery");
        var antiforgeryCookie = Assert.Single(
            antiforgery.Headers.GetValues("Set-Cookie"));
        Assert.DoesNotContain(
            "; secure",
            antiforgeryCookie,
            StringComparison.OrdinalIgnoreCase);

        var antiforgeryPayload = await antiforgery.Content
            .ReadFromJsonAsync<AntiforgeryResponse>();
        client.DefaultRequestHeaders.Add(
            "X-ReachCommander-CSRF",
            antiforgeryPayload!.RequestToken);
        var setupCode = await factory.GetFreshSetupCodeAsync();
        var setup = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new
            {
                setupCode,
                username = "dragos",
                password = "a-long-test-password",
            });

        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var sessionCookie = Assert.Single(
            setup.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(
                "ReachCommander.Session=",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            "; secure",
            sessionCookie,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync("/api/sources")).StatusCode);

        client.DefaultRequestHeaders.Remove("X-ReachCommander-CSRF");
        var rejectedMutation = await client.PostAsJsonAsync(
            "/api/auth/password",
            new
            {
                currentPassword = "a-long-test-password",
                newPassword = "a-different-test-password",
            });
        Assert.Equal(HttpStatusCode.BadRequest, rejectedMutation.StatusCode);
    }
```

- [ ] **Step 2: Run the focused tests and confirm the LAN test fails**

Run:

```powershell
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj --filter "FullyQualifiedName~Production_http_keeps_secure_cookies_by_default|FullyQualifiedName~Production_trusted_lan_http_supports_setup_without_disabling_security"
```

Expected: the secure-default test passes and the trusted-LAN test fails because its antiforgery cookie still contains `Secure`.

- [ ] **Step 3: Make both cookie registrations use the explicit configuration policy**

In `AuthenticationConfiguration.cs`, calculate the policy once at the beginning of `AddReachCommanderAuthentication`:

```csharp
        var cookieSecurePolicy = CookieSecurePolicy(configuration, environment);
```

Replace both existing assignments with:

```csharp
                options.Cookie.SecurePolicy = cookieSecurePolicy;
```

and:

```csharp
            options.Cookie.SecurePolicy = cookieSecurePolicy;
```

Replace the existing helper with:

```csharp
    private static CookieSecurePolicy CookieSecurePolicy(
        IConfiguration configuration,
        IHostEnvironment environment) =>
        environment.IsDevelopment() ||
        environment.IsEnvironment("Testing") ||
        configuration.GetValue<bool>("Authentication:AllowInsecureHttp")
            ? Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest
            : Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
```

- [ ] **Step 4: Run authentication tests**

Run:

```powershell
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj --filter FullyQualifiedName~AuthenticationApiTests
```

Expected: all `AuthenticationApiTests` pass, including existing proxy, anonymous-access, antiforgery, and rate-limit coverage.

- [ ] **Step 5: Commit the backend policy**

```powershell
git add src/ReachCommander.Api/Authentication/AuthenticationConfiguration.cs tests/ReachCommander.IntegrationTests/AuthenticationApiTests.cs
git commit -m "feat: allow explicit trusted LAN HTTP cookies"
```

---

### Task 2: Validated access-mode deployment schema and Compose wiring

**Files:**
- Modify: `tests/installer/fixtures/valid-request.json`
- Modify: `tests/installer/test_render_config.py`
- Modify: `deploy/render_config.py`
- Modify: `deploy/compose.release.yaml`

**Interfaces:**
- Consumes: existing request/render/add-source/set-image CLI commands.
- Produces: `ACCESS_POLICIES`, request fields `accessMode` and `allowInsecureHttp`, environment keys `REACHCOMMANDER_ACCESS_MODE` and `REACHCOMMANDER_ALLOW_INSECURE_HTTP`, and optional CLI argument `--access-mode` with secure default.

- [ ] **Step 1: Extend the secure fixture and write failing renderer tests**

Add these fields before `bindAddress` in `valid-request.json`:

```json
  "accessMode": "secure-https",
  "allowInsecureHttp": false,
```

Update `test_valid_fixture_loads_as_typed_request` with:

```python
        self.assertEqual("secure-https", request.access_mode)
        self.assertFalse(request.allow_insecure_http)
```

Add these tests to `RendererTestCase`:

```python
    def test_trusted_lan_policy_renders_wildcard_http_configuration(self) -> None:
        renderer = self.require_renderer()
        payload = self.valid_payload()
        payload["accessMode"] = "trusted-lan-http"
        payload["bindAddress"] = "0.0.0.0"
        payload["allowInsecureHttp"] = True

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            request_path = root / "request.json"
            output_path = root / "output"
            request_path.write_text(json.dumps(payload), encoding="utf-8")
            renderer.render_deployment(
                renderer.load_request(request_path), TEMPLATE_PATH, output_path
            )
            environment = (output_path / ".env").read_text(encoding="utf-8")
            compose = (output_path / "compose.yaml").read_text(encoding="utf-8")

        self.assertIn("REACHCOMMANDER_ACCESS_MODE=trusted-lan-http\n", environment)
        self.assertIn("REACHCOMMANDER_BIND_ADDRESS=0.0.0.0\n", environment)
        self.assertIn("REACHCOMMANDER_PORT=8092\n", environment)
        self.assertIn("REACHCOMMANDER_ALLOW_INSECURE_HTTP=true\n", environment)
        self.assertIn(
            'Authentication__AllowInsecureHttp: "${REACHCOMMANDER_ALLOW_INSECURE_HTTP}"',
            compose,
        )

    def test_rejects_contradictory_access_policy(self) -> None:
        cases = (
            ("secure-https", "0.0.0.0", False),
            ("secure-https", "127.0.0.1", True),
            ("trusted-lan-http", "127.0.0.1", True),
            ("trusted-lan-http", "0.0.0.0", False),
            ("unsupported", "127.0.0.1", False),
        )
        for access_mode, bind_address, allow_insecure_http in cases:
            with self.subTest(access_mode=access_mode, bind_address=bind_address):
                payload = self.valid_payload()
                payload["accessMode"] = access_mode
                payload["bindAddress"] = bind_address
                payload["allowInsecureHttp"] = allow_insecure_http
                with self.assertRaisesRegex(ValueError, "accessMode"):
                    self.load_payload(payload)
```

Update manual `.env` text in `test_set_image_changes_only_the_validated_image_key` to include all seven keys in this order and change the line-count assertion to seven:

```python
                "REACHCOMMANDER_ACCESS_MODE=secure-https\n"
                "REACHCOMMANDER_BIND_ADDRESS=127.0.0.1\n"
                "REACHCOMMANDER_PORT=8092\n"
                "REACHCOMMANDER_ALLOW_INSECURE_HTTP=false\n"
                "REACHCOMMANDER_UID=1000\n"
                "REACHCOMMANDER_GID=1000\n"
                "REACHCOMMANDER_IMAGE=ghcr.io/dragosniamtu/reach-commander:stable\n",
```

Replace the two assertions after `set_env_image` with:

```python
            self.assertEqual(7, len(lines))
            self.assertEqual(f"REACHCOMMANDER_IMAGE={new_image}", lines[-1])
            self.assertEqual("REACHCOMMANDER_PORT=8092", lines[2])
```

- [ ] **Step 2: Run renderer tests and confirm schema failures**

Run:

```powershell
python -m unittest tests/installer/test_render_config.py -v
```

Expected: failures report missing `accessMode`/`allowInsecureHttp` dataclass behavior and missing generated environment/Compose settings.

- [ ] **Step 3: Implement strict access-policy validation**

In `render_config.py`, add:

```python
ACCESS_POLICIES = {
    "secure-https": ("127.0.0.1", False),
    "trusted-lan-http": ("0.0.0.0", True),
}
REQUEST_KEYS = {
    "accessMode",
    "bindAddress",
    "port",
    "allowInsecureHttp",
    "uid",
    "gid",
    "image",
    "sources",
}
ENV_KEYS = (
    "REACHCOMMANDER_ACCESS_MODE",
    "REACHCOMMANDER_BIND_ADDRESS",
    "REACHCOMMANDER_PORT",
    "REACHCOMMANDER_ALLOW_INSECURE_HTTP",
    "REACHCOMMANDER_UID",
    "REACHCOMMANDER_GID",
    "REACHCOMMANDER_IMAGE",
)
```

Add these validators:

```python
def _validate_access_mode(value: object) -> str:
    if type(value) is not str or value not in ACCESS_POLICIES:
        raise ValueError("accessMode: invalid value")
    return value


def _validate_access_policy(
    access_mode: str,
    bind_address: str,
    allow_insecure_http: bool,
) -> None:
    expected_bind, expected_insecure = ACCESS_POLICIES[access_mode]
    if bind_address != expected_bind or allow_insecure_http is not expected_insecure:
        raise ValueError("accessMode: inconsistent network policy")
```

Change `DeploymentRequest` fields to:

```python
class DeploymentRequest:
    access_mode: str
    bind_address: str
    port: int
    allow_insecure_http: bool
    uid: int
    gid: int
    image: str
    sources: tuple[SourceRequest, ...]
```

Replace `_validate_common` with:

```python
def _validate_common(mapping: dict) -> tuple[str, str, int, bool, int, int, str]:
    access_mode = _validate_access_mode(mapping["accessMode"])
    bind_address = _validate_bind_address(mapping["bindAddress"])
    allow_insecure_http = _require_boolean(
        mapping["allowInsecureHttp"], "allowInsecureHttp"
    )
    _validate_access_policy(access_mode, bind_address, allow_insecure_http)
    return (
        access_mode,
        bind_address,
        _require_integer(mapping["port"], 1, 65535, "port"),
        allow_insecure_http,
        _require_integer(mapping["uid"], 1, 2147483647, "uid"),
        _require_integer(mapping["gid"], 1, 2147483647, "gid"),
        validate_image(mapping["image"]),
    )
```

Preserve existing callers by adding the access mode as the last argument with a secure default:

```python
def create_request(
    output: pathlib.Path,
    bind_address: str,
    port: int,
    uid: int,
    gid: int,
    image: str,
    access_mode: str = "secure-https",
) -> None:
    validated_mode = _validate_access_mode(access_mode)
    expected_bind, allow_insecure_http = ACCESS_POLICIES[validated_mode]
    if bind_address != expected_bind:
        raise ValueError("accessMode: inconsistent network policy")
    mapping = {
        "accessMode": validated_mode,
        "bindAddress": bind_address,
        "port": port,
        "allowInsecureHttp": allow_insecure_http,
        "uid": uid,
        "gid": gid,
        "image": image,
        "sources": [],
    }
    _validate_common(mapping)
    atomic_write(output, _json_document(mapping))
```

Add the parser option:

```python
    create.add_argument(
        "--access-mode",
        choices=tuple(ACCESS_POLICIES),
        default="secure-https",
    )
```

Pass `args.access_mode` as the final argument in the `create_request` call in `main`.

- [ ] **Step 4: Render and validate the seven-key environment**

Prefix the generated environment in `render_deployment` with:

```python
        f"REACHCOMMANDER_ACCESS_MODE={request.access_mode}\n"
        f"REACHCOMMANDER_BIND_ADDRESS={request.bind_address}\n"
        f"REACHCOMMANDER_PORT={request.port}\n"
        f"REACHCOMMANDER_ALLOW_INSECURE_HTTP={'true' if request.allow_insecure_http else 'false'}\n"
```

In `_read_env`, parse and validate the new fields before integer validation:

```python
    access_mode = _validate_access_mode(values["REACHCOMMANDER_ACCESS_MODE"])
    bind_address = _validate_bind_address(values["REACHCOMMANDER_BIND_ADDRESS"])
    allow_insecure_value = values["REACHCOMMANDER_ALLOW_INSECURE_HTTP"]
    if allow_insecure_value not in ("true", "false"):
        raise ValueError("env: invalid boolean")
    _validate_access_policy(
        access_mode,
        bind_address,
        allow_insecure_value == "true",
    )
```

Add to `compose.release.yaml` under `environment`:

```yaml
      Authentication__AllowInsecureHttp: "${REACHCOMMANDER_ALLOW_INSECURE_HTTP}"
```

- [ ] **Step 5: Run renderer and management-command contracts**

Run:

```powershell
python -m unittest tests/installer/test_render_config.py -v
bash tests/installer/test_command.sh
```

Expected: both suites pass; existing create-request callers receive secure defaults and `set-image` preserves all seven validated environment keys.

- [ ] **Step 6: Commit the deployment schema**

```powershell
git add deploy/render_config.py deploy/compose.release.yaml tests/installer/fixtures/valid-request.json tests/installer/test_render_config.py
git commit -m "feat: validate deployment access modes"
```

---

### Task 3: Deterministic LAN display-address discovery and packaging

**Files:**
- Create: `tests/installer/test_lan_address.py`
- Create: `deploy/lan_address.py`
- Modify: `deploy/package-installer.sh`
- Modify: `tests/installer/test_package.sh`
- Modify: `.github/workflows/ci.yml`
- Modify: `tests/installer/workflow-contract.test.mjs`

**Interfaces:**
- Consumes: JSON returned by `ip -j -4 address show up`, JSON returned by `ip -j -4 route show default`, and physical interface names derived from `/sys/class/net/*/device`.
- Produces: `discover_display_addresses(addresses: object, routes: object, physical_interfaces: Collection[str]) -> tuple[str, ...]`; CLI writes zero or more RFC1918 addresses, one per line, and exits zero when discovery is unavailable.

- [ ] **Step 1: Write deterministic discovery tests**

Create `tests/installer/test_lan_address.py` with these imports, loader, setup, and fixtures:

```python
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
```

No socket, DNS, HTTP, or Internet API may appear in the tests or implementation.

- [ ] **Step 2: Run discovery tests and confirm the module is missing**

Run:

```powershell
python -m unittest tests/installer/test_lan_address.py -v
```

Expected: failure reports that `deploy/lan_address.py` does not exist.

- [ ] **Step 3: Implement the pure selector and non-fatal CLI**

Create `deploy/lan_address.py` with these public boundaries and standard-library behavior:

```python
#!/usr/bin/env python3
"""Print useful private-LAN addresses for installer completion output."""

from __future__ import annotations

import ipaddress
import json
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
    return (
        _ip_json("address", "show", "up"),
        _ip_json("route", "show", "default"),
        _physical_interfaces(),
    )


def main() -> int:
    addresses, routes, physical_interfaces = system_snapshot()
    for address in discover_display_addresses(
        addresses, routes, physical_interfaces
    ):
        print(address)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
```

- [ ] **Step 4: Add the helper to the deterministic installer bundle**

In `package-installer.sh`, add this source to `required_sources`:

```bash
  "$SCRIPT_DIRECTORY/lan_address.py"
```

Copy and normalize it with:

```bash
install -m 0644 -- "$SCRIPT_DIRECTORY/lan_address.py" "$PACKAGE_ROOT/lan_address.py"
```

Add `$PACKAGE_ROOT/lan_address.py` to the existing `chmod 0644` block and append it to the deterministic tar with:

```bash
tar "${tar_options[@]}" --mode=0644 -rf "$TAR_TEMPORARY" reachcommander-installer/lan_address.py
```

In `test_package.sh`, add:

```bash
  'reachcommander-installer/lan_address.py'
```

to `expected_entries`, and add `lan_address.py` to the `data_file` permission loop.

- [ ] **Step 5: Add the discovery unit test as a mandatory CI contract**

Add this step immediately before renderer tests in `.github/workflows/ci.yml`:

```yaml
      - name: Test installer LAN address discovery
        run: python3 tools/run_with_annotations.py "Installer LAN address discovery failed" python3 -m unittest tests/installer/test_lan_address.py -v
```

Add the exact unittest command and annotation string to the acceptance-command assertions in `workflow-contract.test.mjs`.

- [ ] **Step 6: Run discovery, package, and workflow contracts**

Run:

```powershell
python -m unittest tests/installer/test_lan_address.py -v
bash tests/installer/test_package.sh
node --test tests/installer/workflow-contract.test.mjs
```

Expected: all tests pass, the archive is byte-for-byte deterministic, and CI cannot omit LAN discovery tests.

- [ ] **Step 7: Commit discovery and packaging**

```powershell
git add deploy/lan_address.py deploy/package-installer.sh tests/installer/test_lan_address.py tests/installer/test_package.sh .github/workflows/ci.yml tests/installer/workflow-contract.test.mjs
git commit -m "feat: discover trusted LAN display addresses"
```

---

### Task 4: Ubuntu installer mode selection, completion URL, and rollback contracts

**Files:**
- Modify: `tests/installer/fake-bin/ip`
- Modify: `tests/installer/test_install.sh`
- Modify: `deploy/install.sh`

**Interfaces:**
- Consumes: `deploy/lan_address.py`, renderer `--access-mode`, existing transactional reconfiguration and health rollback.
- Produces: installer variables `ACCESS_MODE` and `BIND_ADDRESS`; acknowledgement `I understand LAN HTTP is unencrypted`; mode-specific completion output.

- [ ] **Step 1: Add deterministic fake `ip` and failing installer cases**

Create `tests/installer/fake-bin/ip`:

```bash
#!/usr/bin/env bash
set -Eeuo pipefail

case "$*" in
  '-j -4 address show up')
    printf '%s\n' "${FAKE_IP_ADDRESS_JSON:-[]}"
    ;;
  '-j -4 route show default')
    printf '%s\n' "${FAKE_IP_ROUTE_JSON:-[]}"
    ;;
  *)
    printf 'unsupported fake ip invocation: %s\n' "$*" >&2
    exit 64
    ;;
esac
```

In `test_install.sh`, copy `deploy/lan_address.py` into the fake bundle, make fake `ip` executable, and export:

```bash
export FAKE_IP_ADDRESS_JSON='[{"ifname":"ens18","flags":["UP","LOWER_UP"],"addr_info":[{"family":"inet","local":"192.168.44.12","prefixlen":24,"scope":"global"}]}]'
export FAKE_IP_ROUTE_JSON='[{"dst":"default","dev":"ens18","metric":100}]'
```

Keep existing secure prompt sequences stable by changing the first blank input from the old bind prompt to the new default access-mode prompt. Extend the helper with an eighth mode argument:

```bash
  local access_mode="${8:-1}"
  printf '%s\n' \
    "$access_mode" \
    '' \
    '' \
    '' \
    "$first_name" \
    "$SOURCE_ONE" \
    '' \
    "$first_access" \
    'y' \
    "$second_name" \
    "$SOURCE_TWO" \
    '' \
    "$second_access" \
    'n' \
    "$first_id" \
    "$second_id" \
    "$https_acknowledgement"
```

Add installer assertions for:

```bash
grep -q '^REACHCOMMANDER_ACCESS_MODE=secure-https$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "secure access mode missing"
grep -q '^REACHCOMMANDER_ALLOW_INSECURE_HTTP=false$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "secure HTTP policy missing"
grep -q 'Authentication__AllowInsecureHttp' "$REACHCOMMANDER_TEST_INSTALL_ROOT/compose.yaml" || fail "backend HTTP policy missing"
```

Add a fresh LAN installation using mode `2` and acknowledgement `I understand LAN HTTP is unencrypted`. Assert:

```bash
grep -q '^REACHCOMMANDER_ACCESS_MODE=trusted-lan-http$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "LAN access mode missing"
grep -q '^REACHCOMMANDER_BIND_ADDRESS=0.0.0.0$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "LAN wildcard bind missing"
grep -q '^REACHCOMMANDER_PORT=8092$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "LAN host port missing"
grep -q '^REACHCOMMANDER_ALLOW_INSECURE_HTTP=true$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "LAN HTTP policy missing"
grep -q 'http://192.168.44.12:8092' "$TEST_ROOT/lan-install.out" || fail "detected LAN URL missing"
grep -q 'http://<server-lan-ip>:8092' "$TEST_ROOT/lan-install.out" || fail "generic LAN URL missing"
```

Use these complete pre-install cases before the existing secure baseline installation:

```bash
invalid_mode_input="$(source_prompt_input \
  'Family Media' 'RO' 'Movies' 'RW' 'family-media' 'movies' \
  'I have HTTPS' '9')"
run_installer "$invalid_mode_input" "$TEST_ROOT/invalid-access-mode.out"
(( last_status != 0 )) || fail "invalid access mode must fail"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "invalid mode installed deployment"
pass "installer rejects an invalid network access mode before writes"

wrong_lan_ack_input="$(source_prompt_input \
  'Family Media' 'RO' 'Movies' 'RW' 'family-media' 'movies' \
  'I trust my LAN' '2')"
run_installer "$wrong_lan_ack_input" "$TEST_ROOT/wrong-lan-ack.out"
(( last_status != 0 )) || fail "wrong LAN acknowledgement must fail"
[[ ! -e "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" ]] || fail "wrong LAN acknowledgement installed deployment"
pass "installer requires the exact trusted LAN HTTP acknowledgement"

lan_install_input="$(source_prompt_input \
  'Family Media' 'RO' 'Movies' 'RW' 'family-media' 'movies' \
  'I understand LAN HTTP is unencrypted' '2')"
run_installer "$lan_install_input" "$TEST_ROOT/lan-install.out"
assert_equal "0" "$last_status" "trusted LAN installation status"
grep -q '^REACHCOMMANDER_ACCESS_MODE=trusted-lan-http$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "LAN access mode missing"
grep -q '^REACHCOMMANDER_BIND_ADDRESS=0.0.0.0$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "LAN wildcard bind missing"
grep -q '^REACHCOMMANDER_PORT=8092$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "LAN host port missing"
grep -q '^REACHCOMMANDER_ALLOW_INSECURE_HTTP=true$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "LAN HTTP policy missing"
grep -q 'http://192.168.44.12:8092' "$TEST_ROOT/lan-install.out" || fail "detected LAN URL missing"
grep -q 'http://<server-lan-ip>:8092' "$TEST_ROOT/lan-install.out" || fail "generic LAN URL missing"
rm -rf -- "$REACHCOMMANDER_TEST_INSTALL_ROOT" "$REACHCOMMANDER_TEST_COMMAND_PATH"
mkdir -p "$(dirname -- "$REACHCOMMANDER_TEST_COMMAND_PATH")"
pass "trusted LAN mode publishes the Radarr-style host port and completion URLs"
```

Change the existing unhealthy reconfiguration case at the end of `test_install.sh` so the candidate configuration switches from secure mode to LAN mode:

```bash
deployment_before="$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f ! -name command.lock -print0 | sort -z | xargs -0 sha256sum)"
authentication_before="$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT/data" -type f -print0 | sort -z | xargs -0 sha256sum)"
command_before="$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")"
printf 'unhealthy\nhealthy\n' >"$TEST_ROOT/health-sequence"
export FAKE_DOCKER_HEALTH_FILE="$TEST_ROOT/health-sequence"
reconfigure_input=$'y\n'"$(source_prompt_input \
  'Changed Media' 'RO' 'Changed Movies' 'RW' 'changed-media' 'changed-movies' \
  'I understand LAN HTTP is unencrypted' '2')"
run_installer "$reconfigure_input" "$TEST_ROOT/reconfigure-failure.out"
(( last_status != 0 )) || fail "unhealthy LAN reconfiguration must report failure"
assert_equal "$deployment_before" "$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT" -type f ! -name command.lock -print0 | sort -z | xargs -0 sha256sum)" "rolled-back deployment"
assert_equal "$authentication_before" "$(find "$REACHCOMMANDER_TEST_INSTALL_ROOT/data" -type f -print0 | sort -z | xargs -0 sha256sum)" "rolled-back authentication data"
assert_equal "$command_before" "$(sha256sum "$REACHCOMMANDER_TEST_COMMAND_PATH")" "rolled-back command"
grep -q '^REACHCOMMANDER_ACCESS_MODE=secure-https$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "rollback did not restore secure mode"
grep -q '^REACHCOMMANDER_ALLOW_INSECURE_HTTP=false$' "$REACHCOMMANDER_TEST_INSTALL_ROOT/.env" || fail "rollback did not restore secure cookie policy"
unset FAKE_DOCKER_HEALTH_FILE
pass "unhealthy LAN reconfiguration restores the secure deployment and durable state"
```

- [ ] **Step 2: Run installer contracts and confirm prompt/schema failures**

Run:

```powershell
bash tests/installer/test_install.sh
```

Expected: new LAN cases fail because the installer still prompts for a free-form bind address and always requires `I have HTTPS`.

- [ ] **Step 3: Add access-mode collection and acknowledgement**

At the top of `install.sh`, add:

```bash
LAN_ADDRESS_HELPER="$SCRIPT_DIRECTORY/lan_address.py"
ACCESS_MODE=''
BIND_ADDRESS=''
```

Add `$LAN_ADDRESS_HELPER` to `require_bundle`.

Replace the bind prompt with:

```bash
printf 'Network access mode:\n'
printf '  1. Secure HTTPS reverse proxy (recommended)\n'
printf '  2. Direct HTTP on trusted LAN\n'
prompt_value 'Select network access mode' '1'
case "$REPLY_VALUE" in
  1)
    ACCESS_MODE='secure-https'
    BIND_ADDRESS='127.0.0.1'
    ;;
  2)
    ACCESS_MODE='trusted-lan-http'
    BIND_ADDRESS='0.0.0.0'
    printf 'WARNING: trusted LAN HTTP listens on every host interface.\n' >&2
    printf 'Credentials, cookies, filenames, and file contents are not encrypted in transit.\n' >&2
    printf 'Do not expose this port through router forwarding or a public interface.\n' >&2
    ;;
  *)
    rc_die 'network access mode must be 1 or 2'
    exit 1
    ;;
esac
```

Replace the unconditional HTTPS acknowledgement with:

```bash
printf 'ReachCommander includes its own administrator login; proxy authentication is optional.\n'
if [[ "$ACCESS_MODE" == 'secure-https' ]]; then
  prompt_value "Type 'I have HTTPS' to confirm encrypted transport" ''
  if [[ "$REPLY_VALUE" != 'I have HTTPS' ]]; then
    rc_die 'HTTPS acknowledgement did not match'
    exit 1
  fi
else
  prompt_value "Type 'I understand LAN HTTP is unencrypted' to continue" ''
  if [[ "$REPLY_VALUE" != 'I understand LAN HTTP is unencrypted' ]]; then
    rc_die 'trusted LAN HTTP acknowledgement did not match'
    exit 1
  fi
fi
```

- [ ] **Step 4: Pass the mode to the renderer and print Radarr-style URLs**

Add to `write_request` immediately after `create-request`:

```bash
    --access-mode "$ACCESS_MODE" \
```

Keep `--bind-address "$BIND_ADDRESS"` and `--port "$PORT"` unchanged.

Replace the final unconditional messages with:

```bash
if [[ "$ACCESS_MODE" == 'trusted-lan-http' ]]; then
  printf 'ReachCommander is ready on the trusted LAN:\n'
  detected_addresses=''
  if detected_addresses="$(python3 "$LAN_ADDRESS_HELPER" 2>/dev/null)"; then
    while IFS= read -r detected_address; do
      [[ -n "$detected_address" ]] || continue
      printf '  http://%s:%s\n' "$detected_address" "$PORT"
    done <<<"$detected_addresses"
  fi
  printf '  http://<server-lan-ip>:%s\n' "$PORT"
  printf 'Trusted LAN HTTP is unencrypted and listens on every host interface.\n'
else
  printf 'ReachCommander is healthy at http://127.0.0.1:%s\n' "$PORT"
  printf 'Publish this endpoint through an HTTPS reverse proxy; proxy authentication is optional.\n'
fi
printf 'Run reachcommander doctor to verify the deployment.\n'
```

- [ ] **Step 5: Run installer, renderer, and ShellCheck contracts**

Run:

```powershell
bash tests/installer/test_install.sh
python -m unittest tests/installer/test_render_config.py tests/installer/test_lan_address.py -v
shellcheck -x --source-path=SCRIPTDIR deploy/install.sh tests/installer/test_install.sh tests/installer/fake-bin/ip
```

Expected: all suites pass; secure input remains backward-compatible, LAN mode prints the detected and generic URLs, and rollback preserves previous state.

- [ ] **Step 6: Commit installer behavior**

```powershell
git add deploy/install.sh tests/installer/test_install.sh tests/installer/fake-bin/ip
git commit -m "feat: add Ubuntu trusted LAN HTTP mode"
```

---

### Task 5: Doctor validation and operator documentation

**Files:**
- Modify: `tests/installer/test_command.sh`
- Modify: `deploy/reachcommander`
- Modify: `tests/installer/docs-contract.test.mjs`
- Modify: `docs/deployment/ubuntu.md`
- Modify: `README.md`
- Modify: `docs/INSTALL.md`
- Modify: `deploy/README.md`
- Modify: `SECURITY.md`

**Interfaces:**
- Consumes: the seven-key `.env` access policy produced by the renderer.
- Produces: doctor pass/warn/fail messages and public operator guidance consistent with wildcard LAN publication.

- [ ] **Step 1: Write failing doctor and documentation contracts**

Replace the old generic non-loopback doctor assertion in `test_command.sh` with a consistent trusted-LAN environment:

```bash
sed \
  -e 's/^REACHCOMMANDER_ACCESS_MODE=.*/REACHCOMMANDER_ACCESS_MODE=trusted-lan-http/' \
  -e 's/^REACHCOMMANDER_BIND_ADDRESS=.*/REACHCOMMANDER_BIND_ADDRESS=0.0.0.0/' \
  -e 's/^REACHCOMMANDER_ALLOW_INSECURE_HTTP=.*/REACHCOMMANDER_ALLOW_INSECURE_HTTP=true/' \
  "$TEST_ROOT/env.backup" >"$INSTALL_ROOT/.env"
run_command doctor
assert_equal "0" "$last_status" "doctor trusted LAN status"
[[ "$last_output" == *'[WARN] Trusted LAN HTTP listens on every host interface'* ]] || fail "doctor trusted LAN warning missing"
```

Then corrupt only `REACHCOMMANDER_ALLOW_INSECURE_HTTP` to false and assert doctor exits `1` with:

```text
[FAIL] Network access policy is inconsistent
```

Add a documentation contract requiring all of these phrases across the Ubuntu guide, README, installer overview, and security policy:

```javascript
for (const required of [
  'Direct HTTP on trusted LAN',
  'http://<server-lan-ip>:<port>',
  '8092',
  '8080',
  'all host interfaces',
  'DHCP',
  'Authentication__AllowInsecureHttp',
  'authentication remains enabled',
  'antiforgery remains enabled',
  'rate limiting remains enabled',
  'router forwarding',
  'PWA installation requires HTTPS',
]) {
  assert.ok(content.includes(required), `trusted LAN docs are missing: ${required}`);
}
```

- [ ] **Step 2: Run doctor and docs contracts and confirm failures**

Run:

```powershell
bash tests/installer/test_command.sh
node --test tests/installer/docs-contract.test.mjs
```

Expected: doctor still emits the generic non-loopback warning and public docs lack the complete trusted-LAN contract.

- [ ] **Step 3: Make doctor validate the complete access policy**

In `deploy/reachcommander`, replace the bind-only doctor branch with:

```bash
  access_mode=''
  bind_address=''
  allow_insecure_http=''
  if access_mode="$(read_env_value REACHCOMMANDER_ACCESS_MODE)" &&
    bind_address="$(read_env_value REACHCOMMANDER_BIND_ADDRESS)" &&
    allow_insecure_http="$(read_env_value REACHCOMMANDER_ALLOW_INSECURE_HTTP)"; then
    case "$access_mode:$bind_address:$allow_insecure_http" in
      'secure-https:127.0.0.1:false')
        doctor_pass 'Secure HTTPS upstream is loopback-only'
        ;;
      'trusted-lan-http:0.0.0.0:true')
        doctor_warn 'Trusted LAN HTTP listens on every host interface'
        ;;
      *)
        doctor_fail 'Network access policy is inconsistent'
        ;;
    esac
  else
    doctor_fail 'Network access policy is missing or duplicated'
  fi
```

Declare the three locals with the other `command_doctor` locals if that function already declares them at its beginning.

- [ ] **Step 4: Update operator documentation without weakening secure defaults**

In `docs/deployment/ubuntu.md`:

- change the opening security boundary to say HTTPS loopback is the default and trusted-LAN HTTP is an explicit exception;
- replace the free-form bind-address installer bullet with the two numbered access modes;
- document host port `8092`, container port `8080`, and `http://<server-lan-ip>:<port>`;
- state that wildcard LAN mode listens on all host interfaces and survives DHCP changes without reconfiguration;
- state that `Authentication__AllowInsecureHttp=true` changes only cookie transport;
- state explicitly that authentication, authorization, antiforgery, and rate limiting remain enabled;
- warn that credentials and cookies are observable over unencrypted LAN HTTP;
- warn against public interfaces and router forwarding;
- keep the HTTPS reverse-proxy examples and trusted-forwarded-header explanation unchanged;
- state that ordinary browser use works over LAN HTTP but PWA installation requires HTTPS; and
- explain that rerunning the installer can switch modes without losing accounts, keys, sources, or application state.

Use this canonical LAN section so the security promises are unambiguous:

```markdown
### Direct HTTP on trusted LAN

Choose **Direct HTTP on trusted LAN** only for a network you control. Docker publishes host port `8092` on all host interfaces and forwards it to ReachCommander's container port `8080`. Open `http://<server-lan-ip>:<port>` from another device; the default is `http://<server-lan-ip>:8092`.

This Radarr-style wildcard publication does not save one DHCP address, so a later DHCP change does not require ReachCommander reconfiguration. The installer detects RFC1918 addresses only to print convenient URLs. It does not configure Ubuntu firewall rules, router forwarding, DNS, certificates, or a private-interface boundary.

`Authentication__AllowInsecureHttp=true` changes cookie transport only. Administrator authentication remains enabled, authorization remains enabled, antiforgery remains enabled, and rate limiting remains enabled in Production. HTTP does not encrypt credentials, cookies, filenames, or file contents. Never forward this port from a router or expose it through a public host interface; use the default HTTPS reverse-proxy mode or a trusted VPN instead.

Ordinary browser access works over LAN HTTP. PWA installation requires HTTPS because production service workers require a secure context. Rerun the checksum-verified installer to change access mode or port; reconfiguration preserves the administrator account, Data Protection keys, sources, durable operations, and application state.
```

Update `README.md`, `docs/INSTALL.md`, `deploy/README.md`, and `SECURITY.md` with the same short boundary: secure loopback remains recommended; Ubuntu offers explicit wildcard trusted-LAN HTTP; host `8092` maps to container `8080`; authentication protections remain; public forwarding is unsafe; PWA installation still requires HTTPS.

Use this concise wording, adapted only for surrounding grammar:

```markdown
Ubuntu installations default to a loopback-only HTTPS reverse-proxy upstream. The explicit **Direct HTTP on trusted LAN** mode publishes host port `8092` on all host interfaces and forwards it to container port `8080`; open `http://<server-lan-ip>:<port>`. Authentication, authorization, antiforgery, and rate limiting remain enabled, but transport is unencrypted. Do not enable router forwarding or public exposure. DHCP changes need no reconfiguration; PWA installation requires HTTPS.
```

- [ ] **Step 5: Run doctor and documentation contracts**

Run:

```powershell
bash tests/installer/test_command.sh
node --test tests/installer/docs-contract.test.mjs
```

Expected: both suites pass, inconsistent access policies fail doctor, and published documentation contains no machine-specific address outside labeled examples.

- [ ] **Step 6: Commit diagnostics and documentation**

```powershell
git add deploy/reachcommander tests/installer/test_command.sh docs/deployment/ubuntu.md README.md docs/INSTALL.md deploy/README.md SECURITY.md tests/installer/docs-contract.test.mjs
git commit -m "docs: explain trusted LAN HTTP deployment"
```

---

### Task 6: Hardened smoke coverage and final verification

**Files:**
- Modify: `.github/workflows/ci.yml`
- Modify: `tests/installer/workflow-contract.test.mjs`

**Interfaces:**
- Consumes: renderer secure and LAN modes, Compose template, release image, complete test suites.
- Produces: a publication gate proving both generated network policies validate before multi-architecture publication.

- [ ] **Step 1: Add a failing workflow contract for both rendered modes**

In `workflow-contract.test.mjs`, extend the hardened-smoke test to require both arguments and both generated values:

```javascript
  assert.match(smoke, /--access-mode[\s\\]+secure-https/);
  assert.match(smoke, /--access-mode[\s\\]+trusted-lan-http/);
  assert.match(smoke, /REACHCOMMANDER_BIND_ADDRESS=0\.0\.0\.0/);
  assert.match(smoke, /REACHCOMMANDER_ALLOW_INSECURE_HTTP=true/);
  assert.match(smoke, /Authentication__AllowInsecureHttp/);
```

- [ ] **Step 2: Run the workflow contract and confirm the smoke job is incomplete**

Run:

```powershell
node --test tests/installer/workflow-contract.test.mjs
```

Expected: the hardened-smoke test fails because the job renders only the existing secure request.

- [ ] **Step 3: Render and validate both modes in hardened smoke**

In `.github/workflows/ci.yml`, make the existing real-container request explicit with:

```yaml
            --access-mode secure-https \
            --bind-address 127.0.0.1 \
```

Before starting the secure container, create a second request/output under the job temporary root using:

```bash
          python3 deploy/render_config.py create-request \
            --output "$SMOKE_ROOT/lan-request.json" \
            --access-mode trusted-lan-http \
            --bind-address 0.0.0.0 \
            --port 8092 \
            --uid 1000 \
            --gid 1000 \
            --image "$IMAGE"
```

Add `source-a` and `source-b` and render the LAN deployment with:

```bash
          python3 deploy/render_config.py add-source \
            --request "$SMOKE_ROOT/lan-request.json" \
            --id source-a \
            --name 'Source A' \
            --host-path "$SMOKE_ROOT/source-a" \
            --access rw \
            --default-left true \
            --default-right false
          python3 deploy/render_config.py add-source \
            --request "$SMOKE_ROOT/lan-request.json" \
            --id source-b \
            --name 'Source B' \
            --host-path "$SMOKE_ROOT/source-b" \
            --access rw \
            --default-left false \
            --default-right true
          python3 deploy/render_config.py render \
            --request "$SMOKE_ROOT/lan-request.json" \
            --template deploy/compose.release.yaml \
            --output "$SMOKE_ROOT/lan-deployment"
```

Then validate:

```bash
          grep -Fx 'REACHCOMMANDER_BIND_ADDRESS=0.0.0.0' "$SMOKE_ROOT/lan-deployment/.env"
          grep -Fx 'REACHCOMMANDER_ALLOW_INSECURE_HTTP=true' "$SMOKE_ROOT/lan-deployment/.env"
          grep -F 'Authentication__AllowInsecureHttp' "$SMOKE_ROOT/lan-deployment/compose.yaml"
          docker compose --project-directory "$SMOKE_ROOT/lan-deployment" config --quiet
```

Keep the actual container startup on the secure request so GitHub-hosted runner networking is not presented as a real-LAN test.

- [ ] **Step 4: Run the complete local verification matrix**

Run:

```powershell
dotnet test ReachCommander.slnx
python -m unittest tests/installer/test_lan_address.py tests/installer/test_render_config.py tests/installer/test_updater_protocol.py tests/installer/test_updater_service.py -v
bash tests/installer/test_common.sh
bash tests/installer/test_install.sh
bash tests/installer/test_command.sh
bash tests/installer/test_package.sh
node --test tests/installer/release-tags.test.mjs tests/installer/workflow-contract.test.mjs tests/installer/docs-contract.test.mjs
shellcheck -x --source-path=SCRIPTDIR deploy/install.sh deploy/reachcommander deploy/lib/common.sh deploy/package-installer.sh tests/installer/test_common.sh tests/installer/test_install.sh tests/installer/test_command.sh tests/installer/test_package.sh tests/installer/fake-bin/ip
```

Expected: every command exits `0`; no test disables authentication, antiforgery, authorization, or rate limiting.

- [ ] **Step 5: Review the final diff and unrelated-file boundary**

Run:

```powershell
git diff --check
git status --short
git diff --stat
```

Expected: no whitespace errors; only planned paths are modified; `NC-theme.png` remains untracked and unstaged.

- [ ] **Step 6: Commit smoke coverage**

```powershell
git add .github/workflows/ci.yml tests/installer/workflow-contract.test.mjs
git commit -m "test: gate trusted LAN deployment modes"
```

- [ ] **Step 7: Push `master` and verify GitHub Actions**

```powershell
git push origin master
$runId = gh run list --branch master --limit 1 --json databaseId --jq '.[0].databaseId'
gh run watch $runId --exit-status
gh run view $runId
```

Expected: the new CI run starts for the pushed commit and exits successfully. Require backend Ubuntu/Windows, frontend/browser acceptance, macOS contracts, installer acceptance, hardened amd64 smoke, and verified multi-architecture publication to succeed before reporting completion.
