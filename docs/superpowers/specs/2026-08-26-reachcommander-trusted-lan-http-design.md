# ReachCommander Radarr-Style Trusted LAN HTTP Design

**Date:** 2026-08-26

**Status:** Approved

**Target:** Ubuntu installer-managed Docker deployments

## Problem

ReachCommander currently treats its production endpoint as an HTTPS reverse-proxy upstream. The Ubuntu installer binds the host port to `127.0.0.1`, requires an HTTPS acknowledgement, and configures production authentication and antiforgery cookies as HTTPS-only. This is secure by default, but it does not provide the familiar home-server experience used by the *arr family: install the service, then open the server's LAN address and conventional port from another device.

The desired optional experience is:

```text
http://<server-lan-ip>:8092
```

The application must remain in the Production environment with its built-in administrator authentication, authorization, antiforgery validation, and rate limiting enabled. The host-facing port must differ from the container's internal port `8080`.

## Superseded exact-address requirement

The initial requirements prohibited wildcard binding, required selection and persistence of one host-assigned RFC1918 address, and required reconfiguration after a DHCP address change. After reviewing Radarr's actual behavior, the user explicitly replaced those requirements with the Radarr-style model.

Trusted LAN HTTP therefore publishes the host port on all host interfaces, equivalent to Docker Compose `8092:8080`. It does not select or persist one LAN address, and DHCP address changes do not require ReachCommander reconfiguration. Best-effort LAN address discovery is used only to print a convenient URL.

Secure HTTPS mode remains loopback-only and remains the default.

## Goals

- Add an explicit `Direct HTTP on trusted LAN` option to the Ubuntu installer.
- Keep `Secure HTTPS reverse proxy` as the recommended default.
- Publish LAN mode as host port `8092` to container port `8080` on all host interfaces.
- Print a useful `http://<server-lan-ip>:8092` completion URL without an Internet lookup.
- Preserve Production environment behavior and all existing authentication, authorization, antiforgery, and rate-limiting protections.
- Permit authentication and antiforgery cookies over HTTP only when LAN mode was explicitly selected.
- Keep the insecure-HTTP setting false when absent and false in secure mode.
- Preserve account data, Data Protection keys, sources, durable operations, and other application state during reconfiguration.
- Keep existing HTTPS reverse-proxy and trusted-forwarded-header behavior unchanged.
- Document the LAN trust boundary, firewall/router responsibilities, and PWA secure-context limitation.

## Non-goals

- TLS termination, certificate management, DNS, reverse-proxy installation, VPN setup, or router configuration.
- Automatically changing Ubuntu firewall rules or opening a router port.
- Restricting LAN mode to one interface or one private address.
- Supporting public Internet exposure over HTTP.
- Disabling or bypassing administrator authentication, authorization, antiforgery, or rate limiting.
- Changing `ASPNETCORE_ENVIRONMENT` to Development or Testing.
- Persisting the detected LAN address as deployment configuration.
- Rewriting configuration automatically after DHCP or interface changes.
- Expanding this installer change to macOS, Windows, or unmanaged Compose deployments.

## Inspiration and deliberate differences

Radarr defaults its application bind address to `*`, uses a conventional host port, and instructs users to browse to `http://{your_ip_here}:7878`. ReachCommander adopts that networking experience for its optional LAN mode. Radarr's current documentation also treats authentication as mandatory.

ReachCommander deliberately differs in three ways:

1. loopback HTTPS remains the default rather than wildcard HTTP;
2. wildcard HTTP requires an explicit installer selection and warning; and
3. ReachCommander's existing single-administrator setup, antiforgery, and rate-limit model remains authoritative.

References:

- [Radarr bind and port defaults](https://raw.githubusercontent.com/Radarr/Radarr/develop/src/NzbDrone.Core/Configuration/ConfigFileProvider.cs)
- [Servarr Radarr quick start](https://github.com/Servarr/Wiki/blob/master/radarr/quick-start-guide.md)
- [Servarr Radarr host and security settings](https://github.com/Servarr/Wiki/blob/master/radarr/settings.md)

## Considered approaches

### Radarr-style wildcard publication — selected

Docker publishes host port `8092` on every host interface and forwards it to container port `8080`. The deployment is independent of a particular DHCP lease and behaves like other home-server applications. The installer may display a detected private address but does not use it as a security boundary.

### Exact RFC1918 host-address publication — rejected by user

Publishing `<selected-private-ip>:8092:8080` limits exposure to one interface and was the initial recommendation. It requires address selection, persistence, and reconfiguration after DHCP changes, so it does not match the requested Radarr experience.

### Runtime application address discovery — rejected

Having the ASP.NET Core process discover a host LAN address cannot control Docker's host port publication and obscures an important exposure decision. Container and host network identities are separate, so this would add complexity without producing the desired behavior.

## Installer experience

The Ubuntu installer presents:

```text
Network access mode:

1. Secure HTTPS reverse proxy (recommended)
2. Direct HTTP on trusted LAN
```

### Secure HTTPS reverse proxy

- Selected by default.
- Publishes `127.0.0.1:8092:8080`, or the chosen host port in place of `8092`.
- Persists `Authentication__AllowInsecureHttp=false` through generated configuration.
- Retains the exact HTTPS acknowledgement.
- Prints the local upstream URL and existing reverse-proxy guidance.

### Direct HTTP on trusted LAN

- Requires explicit selection.
- Displays a warning that usernames, passwords, cookies, filenames, and file contents can traverse the LAN without transport encryption.
- Requires an explicit confirmation before configuration is committed.
- Publishes `8092:8080`, or the chosen host port in place of `8092`.
- Persists `Authentication__AllowInsecureHttp=true` through generated configuration.
- Prints a best-effort RFC1918 URL and the generic fallback `http://<server-lan-ip>:<port>`.
- Warns not to expose the port through router forwarding or a public interface.

The existing port prompt remains and defaults to `8092`. The application container continues to listen on `8080`; users never browse to the container-only port.

## LAN URL discovery

Address discovery is presentation-only. It must never alter the Compose bind, authentication policy, or installation success when the wildcard port can otherwise start.

The installer uses local kernel routing and interface data and makes no Internet request. The preferred display address is an active RFC1918 IPv4 address on the interface supplying the default route. Loopback, link-local, public, CGNAT, Docker bridge, and other virtual-interface addresses are not suggested when a real private interface is available. Interface names such as `eth0` are never hardcoded.

If one suitable address is found, the completion output includes it. If several equally suitable addresses remain, the installer may print each reachable candidate rather than asking the user to select a bind address. If none is found, installation can still complete and prints only the generic placeholder URL.

Discovery logic is isolated from shell prompting and accepts captured route/address data so it can be tested deterministically on CI hosts without relying on their actual topology.

## Generated configuration

The renderer gains an explicit access mode or equivalent validated boolean and produces these effective values.

Secure mode:

```text
REACHCOMMANDER_ACCESS_MODE=secure-https
REACHCOMMANDER_BIND_ADDRESS=127.0.0.1
REACHCOMMANDER_PORT=8092
REACHCOMMANDER_ALLOW_INSECURE_HTTP=false
```

Trusted LAN mode:

```text
REACHCOMMANDER_ACCESS_MODE=trusted-lan-http
REACHCOMMANDER_BIND_ADDRESS=0.0.0.0
REACHCOMMANDER_PORT=8092
REACHCOMMANDER_ALLOW_INSECURE_HTTP=true
```

The shared Compose template continues to map the exact generated bind and host port to container port `8080`. It passes the generated value as:

```yaml
Authentication__AllowInsecureHttp: "${REACHCOMMANDER_ALLOW_INSECURE_HTTP}"
```

The renderer validates the access mode, bind address, boolean grammar, and their permitted combinations. It rejects contradictory configuration such as `secure-https` with a wildcard bind or `trusted-lan-http` with insecure HTTP disabled. An absent backend setting still evaluates to `false`.

## Backend behavior

The ASP.NET Core authentication registration reads `Authentication:AllowInsecureHttp` from configuration.

- When false or absent, Production authentication and antiforgery cookies use `CookieSecurePolicy.Always`, preserving current HTTPS behavior.
- When true, both cookies use `CookieSecurePolicy.SameAsRequest`, allowing the deliberate HTTP deployment to receive and return its cookies.
- Development and Testing retain their existing test-friendly policy.

This setting affects cookie transport only. It does not change:

- account creation or password verification;
- authentication or authorization requirements;
- session lifetime or invalidation;
- HttpOnly or SameSite settings;
- automatic antiforgery validation;
- antiforgery-token issuance and validation;
- login/setup rate limiting;
- file-operation authorization; or
- forwarded-header trust.

No frontend-only authentication or browser-stored credential mechanism is introduced.

## Security model

Wildcard LAN publication exposes ReachCommander on every IPv4 interface on the Ubuntu host. The detected private address is informational and does not constrain exposure. The installer therefore treats LAN HTTP as a security-sensitive opt-in and does not imply that a private address, firewall, or router has been configured safely.

The operator is responsible for ensuring that port `8092` is reachable only from a trusted network. Documentation recommends host firewall rules, a trusted VPN, or the default HTTPS reverse-proxy mode. It explicitly prohibits treating LAN HTTP as safe for public Internet exposure.

Authentication remains necessary but is not transport encryption. An observer on the same network may be able to capture credentials or session cookies. `SameSite=Strict` and `HttpOnly` remain valuable browser protections but do not prevent network interception over HTTP.

The PWA shell remains installable only in a secure context. Ordinary same-origin browser use works over LAN HTTP, but service-worker installation, offline behavior, and normal PWA installation prompts require HTTPS except on browser-recognized localhost origins.

## Installation and reconfiguration data flow

1. Validate prerequisites and recover any interrupted installer transaction.
2. Detect whether this is a new installation or transactional reconfiguration.
3. Collect the network access mode before the host port.
4. Require the mode-specific acknowledgement.
5. Collect runtime UID/GID and sources through the existing flow.
6. Render the request, `.env`, Compose configuration, and source metadata in the existing staging area.
7. Validate the mode/bind/insecure-HTTP combination and `docker compose config` output.
8. Journal and atomically commit the generated configuration.
9. Start the container and wait for the bounded health check.
10. Roll back an unhealthy reconfiguration through the existing transaction mechanism.
11. Print the mode-appropriate URL and security guidance.

Rerunning the installer after changing modes or ports uses the existing reconfiguration path. Account data, Data Protection keys, configured sources, durable operation state, update state, and other `/data` content remain outside the replaced generated-file set.

## Failure behavior

- An invalid mode, port, bind address, boolean, or contradictory configuration fails before active files are replaced.
- A declined or mistyped LAN warning leaves the deployment unchanged.
- Display-address discovery failure is non-fatal and produces the generic URL.
- Docker port collision or unhealthy startup uses the existing bounded failure and rollback behavior.
- Reconfiguration rollback restores the previous bind, port, cookie policy, and healthy image together.
- A missing `Authentication:AllowInsecureHttp` value fails secure because the backend default is false.
- `reachcommander doctor` reports loopback-only versus all-interface publication and warns clearly for the latter.
- Diagnostics never print passwords, cookies, account JSON, Data Protection keys, or source contents.

## Testing strategy

### Renderer and Compose contracts

- Secure mode renders `127.0.0.1:8092:8080` and insecure HTTP false.
- LAN mode renders wildcard `8092:8080` semantics and insecure HTTP true.
- Host port overrides preserve container port `8080`.
- Invalid mode/bind/boolean combinations are rejected.
- Environment parsing accepts only the complete ordered key set.
- Compose configuration passes the exact backend setting.

### Installer contracts

- Secure mode is the default and still requires the HTTPS acknowledgement.
- LAN mode is explicit and requires its warning confirmation.
- Completion output prints `http://<detected-rfc1918>:8092` when available.
- Discovery uses only controlled local route/interface fixtures.
- Default-route Ethernet and Wi-Fi addresses are preferred.
- All RFC1918 ranges are accepted for display.
- Loopback, link-local, public, CGNAT, Docker, and virtual-only fixtures fall back safely.
- Non-`eth0` interface names work.
- Multiple private interfaces produce deterministic output.
- No Internet availability is required.
- DHCP address changes affect only later completion output, not saved deployment configuration.
- Failed LAN startup restores the previous secure deployment.
- Authentication, keys, sources, and durable-state canaries remain byte-identical during reconfiguration.

### Backend integration

- Production HTTP setup/login succeeds when `Authentication:AllowInsecureHttp=true`.
- Authentication and antiforgery cookies omit `Secure` only for that explicit HTTP mode.
- Production HTTP remains unusable for cookie authentication when the setting is false or absent.
- HTTPS and trusted `X-Forwarded-Proto` behavior remain unchanged.
- Authentication and file APIs still reject anonymous access.
- Mutating requests still require valid antiforgery tokens.
- Login/setup rate limits still return the existing bounded `429` response.

### CI and smoke coverage

The Ubuntu installer suite, renderer tests, backend tests on Ubuntu and Windows, frontend browser acceptance, ShellCheck, documentation contracts, and hardened amd64 container smoke remain publication gates. The hardened smoke verifies both generated modes without claiming that the GitHub runner represents a real home LAN.

## Documentation

`docs/deployment/ubuntu.md` will:

- explain both access modes and identify secure HTTPS as the default;
- show LAN access generically as `http://<server-lan-ip>:<port>`;
- explain `8092` as the host port and `8080` as container-internal;
- state that LAN mode listens on all host interfaces;
- explain that DHCP changes do not require ReachCommander reconfiguration;
- warn against router forwarding and public exposure;
- confirm that authentication, antiforgery, and rate limiting remain enabled;
- describe reconfiguration without account, key, source, or application-state loss; and
- explain the HTTPS requirement for PWA installation and service workers.

The README and deployment overview will use placeholders rather than machine-specific addresses. Any literal private IP appears only in explicitly labeled examples.

## Acceptance criteria

1. On an Ubuntu Docker host, the installer defaults to secure reverse-proxy mode and publishes `127.0.0.1:8092:8080`.
2. After explicitly choosing `Direct HTTP on trusted LAN`, Compose publishes host port `8092` to container port `8080` on all host interfaces.
3. Another device on the same reachable LAN can open `http://<server-lan-ip>:8092` without source edits or a Development environment.
4. The installer prints a detected RFC1918 URL when one can be determined locally and otherwise prints the generic URL.
5. No Internet lookup or hardcoded interface name is used for address display.
6. A DHCP address change does not require ReachCommander reconfiguration because no specific LAN address is persisted.
7. Authentication, authorization, antiforgery validation, and rate limiting remain enabled in LAN mode.
8. The insecure-HTTP cookie policy is enabled only by explicit LAN-mode configuration and defaults to false when absent.
9. Secure HTTPS reverse-proxy behavior, including trusted forwarded-protocol handling, remains unchanged.
10. Reconfiguration preserves administrator data, Data Protection keys, sources, durable operations, and other application state.
11. Invalid or failed reconfiguration restores the previous healthy network and cookie-policy configuration.
12. Documentation clearly identifies wildcard exposure, trusted-LAN limitations, the host/container port distinction, and the PWA HTTPS limitation.
