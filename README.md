# ReachCommander

ReachCommander is a self-hosted, browser-based dual-pane file manager inspired by Total Commander. Milestone 1 is the safe, read-only foundation: configurable filesystem sources, independent panes and directory tabs, dense sortable listings, keyboard navigation, selection, quick filtering, capacity reporting, persistence, and a single-origin container deployment.

> **Trusted-network warning:** Milestone 1 has no authentication, authorization, or built-in TLS. Do not expose it directly to the public internet. Bind it to a trusted network or place it behind an authenticated HTTPS reverse proxy.

## What Milestone 1 includes

- Source buttons above both panes, loaded only from external JSON configuration.
- Independent source, tab, path, cursor, selection, sort, and filter state per pane.
- Browser persistence for pane identity, tabs, logical paths, sorting, and filters.
- Dense details tables with directories first, sortable columns, capacity, read-only, and unavailable-source states.
- Centralized Total Commander-style keyboard handling and a permanent function-key bar.
- Read-only source discovery, directory listing, and file-information APIs.
- Live CPU, memory, storage, GPU, thermal, fan, network, and uptime telemetry when the host exposes it.
- Canonical path confinement with traversal, rooted-path, UNC-path, and symlink-escape rejection.
- ASP.NET Core static SPA hosting, Docker packaging, health checks, and hardened Compose defaults.

Mutation endpoints, transfers, previews, downloads, uploads, authentication, thumbnails, archives, recursive search, and host device mounting are intentionally excluded.

## Architecture

ReachCommander is a modular monolith. Angular and ASP.NET Core are delivered from one origin in production.

```text
Browser
  └─ Angular 22 standalone UI (Signals)
       └─ logical sourceId + relativePath only
            └─ ASP.NET Core 10 API
                 ├─ Application contracts
                 ├─ Domain records
                 └─ Infrastructure
                      ├─ JSON source catalog
                      ├─ canonical path security
                      └─ local read-only filesystem browser
```

The projects are organized as:

```text
src/ReachCommander.Api             HTTP, Problem Details, health, SPA hosting
src/ReachCommander.Application     source/file ports and access errors
src/ReachCommander.Domain          immutable source and file concepts
src/ReachCommander.Infrastructure  configuration, path security, filesystem
client/reach-commander-ui           Angular commander UI
tests/ReachCommander.UnitTests      isolated backend tests
tests/ReachCommander.IntegrationTests HTTP/static-hosting tests
tests/e2e                           deterministic Playwright acceptance flow
```

## Prerequisites

- .NET SDK 10.0.400 or a compatible .NET 10 feature band (`global.json` permits `latestFeature`).
- Node.js 24.15 or newer (or Node 22.22.3+) and npm 10 for Angular 22.
- Docker Engine with Docker Compose v2 for container deployment.
- Chromium installed through Playwright only when running browser tests.

## Source configuration

Production reads `/config/sources.json`. Override it with the ASP.NET Core setting `ReachCommander__SourcesPath`.

```json
{
  "sources": [
    {
      "id": "downloads",
      "name": "Downloads",
      "path": "/sources/downloads",
      "enabled": true,
      "readOnly": true,
      "defaultLeft": true,
      "defaultRight": false
    },
    {
      "id": "media",
      "name": "Media",
      "path": "/sources/media",
      "enabled": true,
      "readOnly": true,
      "defaultLeft": false,
      "defaultRight": true
    }
  ]
}
```

Rules:

- `id` must be unique and use lowercase letters, digits, hyphens, or underscores.
- `name` must not be empty, and `path` must be absolute in the API process/container.
- At least one enabled source is required. At most one enabled source can be each panel's default.
- `enabled: false` hides the source. A missing enabled path remains visible but disabled as unavailable.
- `readOnly` is exposed to the UI. Milestone 1 has no mutation API regardless of this flag.
- Capacity is reported when the platform supports it; an unavailable capacity value does not make a source unavailable.

To add a source, add its JSON record and an explicit bind mount to the same container path, then restart the service. Never mount `/` or `/var/run/docker.sock` for convenience.

## Docker deployment

The checked-in Compose file uses disposable demo roots under `dev-sources/`:

```powershell
docker compose up --build -d
docker compose ps
curl.exe --fail http://localhost:8092/health
curl.exe --fail http://localhost:8092/api/sources
```

Open `http://localhost:8092`. Stop it with:

```powershell
docker compose down
```

For a server, map only the required host directories. The expected mapping is:

```text
Host                    Container
/srv/downloads    ->    /sources/downloads
/srv/media        ->    /sources/media
```

Use these volume entries in a deployment-specific Compose override:

```yaml
services:
  reachcommander:
    volumes:
      - ./config:/config:ro
      - /srv/downloads:/sources/downloads:ro
      - /srv/media:/sources/media:ro
```

The supplied container runs as UID/GID `1000:1000`, drops all Linux capabilities, enables `no-new-privileges`, uses a read-only root filesystem, and provides only a small `/tmp` tmpfs. Ensure UID 1000 can read the mounted host directories. The API listens on container port 8080 and Compose publishes host port 8092.

An optional USB source is already shown in `config/sources.json`; because it has no default bind mount, it demonstrates the disabled/unavailable state. The administrator remains responsible for mounting removable media on the host and binding it to `/sources/usb:ro`.

## Hardware monitoring

The control at the right side of the top bar samples hardware every five seconds and opens a detailed, read-only panel. `GET /api/system-metrics` returns the latest sample. A sample older than 15 seconds is marked stale; unsupported or inaccessible sensors remain explicitly unavailable instead of failing the endpoint. ReachCommander stores no telemetry history and provides no fan, GPU, clock, voltage, or power controls.

For native Windows development, run the API normally:

```powershell
$env:ReachCommander__SourcesPath = (Resolve-Path .\config\sources.local.json).Path
dotnet run --project src/ReachCommander.Api
```

The metrics describe the Windows workstation. Hardware and drivers vary, so temperatures, fan speeds, power, or GPU memory may be unavailable. Docker Desktop instead reports the Linux container/VM view and cannot expose the complete Windows sensor inventory; use the native Windows process when full workstation telemetry matters.

On Ubuntu, a native `dotnet ReachCommander.Api.dll` process reads the host's procfs and sysfs directly. The default hardened Compose deployment remains usable without extra host access, but its metrics describe only the container-visible environment. To opt in to read-only host CPU, memory, uptime, network, thermal, fan, storage, and DRM inventory views, add the hardware override:

```bash
docker compose -f compose.yaml -f compose.hardware.yaml up -d --build
```

`compose.hardware.yaml` mounts only `/proc/stat`, `/proc/meminfo`, `/proc/uptime`, `/proc/net/dev`, and `/sys`, all read-only. Omit this override if container-scoped metrics are sufficient. The base deployment stays unprivileged: it keeps its read-only root filesystem, drops every Linux capability, uses no host PID namespace, and has no Docker socket access.

For Intel or AMD GPU activity, add the DRI device and the host render-group ID:

```bash
RENDER_GID="$(getent group render | cut -d: -f3)" docker compose -f compose.yaml -f compose.hardware.yaml -f compose.hardware.dri.yaml up -d --build
```

This exposes `/dev/dri` and only the render group needed to read supported DRM counters. Omit `compose.hardware.dri.yaml` when no compatible device is present. For NVIDIA, install NVIDIA Container Toolkit on the Ubuntu host and use its runtime injection:

```bash
docker compose -f compose.yaml -f compose.hardware.yaml -f compose.hardware.nvidia.yaml up -d --build
```

Omit the NVIDIA override if the toolkit, device, or compatible NVML library is unavailable; the rest of the metrics continue normally. No vendor command-line tools are invoked.

Hardware telemetry reveals capacity, utilization, network-interface names, and device inventory. Keep ReachCommander on a trusted network and protect it with the same authenticated HTTPS reverse proxy or other deployment access controls used for the file browser.

## Local development

Create a local source file whose paths are absolute for your operating system; do not commit machine-specific paths. For example, save `config/sources.local.json` with roots you are comfortable exposing.

Restore dependencies:

```powershell
dotnet restore ReachCommander.slnx
Push-Location client/reach-commander-ui
npm ci
Pop-Location
```

Run the API in one terminal:

```powershell
$env:ReachCommander__SourcesPath = (Resolve-Path .\config\sources.local.json).Path
dotnet run --project src/ReachCommander.Api --urls http://127.0.0.1:5192
```

Run Angular with its checked-in `/api` proxy in another terminal:

```powershell
Push-Location client/reach-commander-ui
npm start
```

Open `http://localhost:4200`. `Pop-Location` when finished. The API validates the entire source file during startup and refuses to serve with invalid configuration.

To run the production-style single-origin build without Docker:

```powershell
Push-Location client/reach-commander-ui
npm run build
Pop-Location
dotnet publish src/ReachCommander.Api -c Release -o artifacts/publish
$env:ReachCommander__SourcesPath = (Resolve-Path .\config\sources.local.json).Path
dotnet artifacts/publish/ReachCommander.Api.dll --urls http://127.0.0.1:8092
```

## Keyboard and pointer controls

| Input | Action in Milestone 1 |
|---|---|
| `↑` / `↓` | Move cursor |
| `PageUp` / `PageDown` | Move by a page |
| `Home` / `End` | First / last visible item |
| `Enter` | Open the cursor directory; files report that preview is future work |
| `Backspace` | Remove one filter character, otherwise navigate to parent |
| `Tab` | Switch active pane |
| `Insert` | Toggle selection and advance cursor |
| `Ctrl+A` | Select all visible non-parent items |
| `Esc` | Close menu, clear filter, clear selection, then clear status |
| `Ctrl+L` | Focus the active pane's editable logical path |
| `Ctrl+R` | Refresh the active directory |
| `Ctrl+T` | Create a tab at the active path |
| `Ctrl+W` | Close the active tab; the last tab is replaced with a root tab |
| Type while a pane has focus | Append to its quick filter |
| `F9` | Toggle the command reference menu |
| `F3`–`F8` | Reserved/disabled until Milestone 2 |

Pointer selection supports click, `Ctrl+click`, and `Shift+click`. Source buttons, tabs, sortable headings, the path field, quick filter, and F9 also work with pointer/touch input.

## API

```text
GET /api/sources
GET /api/files?sourceId=media&path=/Movies
GET /api/files/info?sourceId=media&path=/Movies/Gladiator%20II.mkv
GET /api/system-metrics
GET /health
```

API errors use `application/problem+json` and stable `code` values such as `invalid_path`, `path_forbidden`, `source_not_found`, `entry_not_found`, and `source_unavailable`. Unknown `/api/*` routes remain JSON 404 responses; client-side routes fall back to the Angular application.

## Security model

- The browser sends a configured source ID and normalized logical path only. Source roots and resolved physical paths are never included in DTOs, client state, or Problem Details.
- The backend rejects NUL characters, relative traversal, rooted paths, drive-qualified paths, and UNC paths.
- Every request is resolved beneath its configured source. Existing path components are canonicalized one by one, symbolic links are resolved, and containment is checked after each resolution and on the final path.
- A symlink that escapes its source is rejected even if the final target exists.
- Milestone 1 exposes no write endpoint. The sample bind mounts are also read-only, providing defense in depth.
- Filesystem exceptions are converted to stable, non-leaking Problem Details. Automated tests use temporary fixture trees, never developer data.

This boundary protects the configured filesystem roots; it is not a substitute for authentication, host permissions, network isolation, TLS, backups, or container hardening.

## Tests and release checks

Backend:

```powershell
dotnet test ReachCommander.slnx -c Release
```

Angular:

```powershell
Push-Location client/reach-commander-ui
npm test -- --watch=false
npm run build
Pop-Location
```

Playwright uses fresh temporary Downloads/Media roots and the real published application:

```powershell
Push-Location tests/e2e
npm ci
npm run install:browsers
npm test
Pop-Location
```

Failed Playwright runs retain screenshots, traces, and an HTML report under `artifacts/`.

## Roadmap

- **Milestone 2 — safe mutations:** confirmation dialogs and secure F4 rename, F5 copy, F6 move, F7 create directory, and F8 delete endpoints. Enforce read-only policy at the application boundary and refresh affected panes.
- **Milestone 3 — resilient transfers:** queued jobs, `BackgroundService`, channels, SignalR progress, throughput/ETA, cancellation, failure recovery, and verified copy-then-delete cross-device moves.
- **Milestone 4 — access control:** local users, password authentication, secure sessions, per-source permissions, read-only roles, settings, and feature flags.
- **Milestone 5 — richer file workflows:** image/video/PDF/text previews, thumbnail mode, recursive search, uploads/downloads, archives, bookmarks, history, and favorites.

The recommended next step is Milestone 2 as a separate security-reviewed slice: define mutation authorization/read-only invariants first, add temporary-filesystem tests for each operation, implement confirmation UX, then expose the function keys one operation at a time.
