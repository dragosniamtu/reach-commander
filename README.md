# ReachCommander

ReachCommander is a self-hosted, browser-based dual-pane file manager inspired by Total Commander. It combines configurable filesystem sources, independent panes and directory tabs, dense sortable listings, wildcard search, controlled batch rename and upload workflows, live hardware telemetry, persistence, and a single-origin container deployment.

> **Trusted-network warning:** ReachCommander has no authentication, authorization, or built-in TLS. Do not expose it directly to the public internet. Bind it to a trusted network or place it behind an authenticated HTTPS reverse proxy.

## What ReachCommander includes

- Source buttons above both panes, loaded only from external JSON configuration.
- Independent source, tab, path, cursor, selection, sort, and filter state per pane.
- Browser persistence for pane identity, tabs, logical paths, sorting, and filters.
- Dense details tables with directories first, sortable columns, capacity, read-only, and unavailable-source states.
- Centralized Total Commander-style keyboard handling and a permanent function-key bar.
- A contextual top toolbar for Multi-Rename, Add files, and active-panel search.
- Explicit `RO`, `RW`, and unavailable source states.
- Read-only source discovery, directory listing, and file-information APIs.
- Server-authoritative Multi-Rename previews, all-or-nothing execution, compensation, and one-level Undo.
- Bounded streamed multi-file uploads with review, progress, cancellation, conflict rejection, and compensation.
- Live CPU, memory, storage, GPU, thermal, fan, network, and uptime telemetry when the host exposes it.
- Canonical path confinement with traversal, rooted-path, UNC-path, and symlink-escape rejection.
- ASP.NET Core static SPA hosting, Docker packaging, health checks, and hardened Compose defaults.

Copy, move, delete, single-item F4 rename, downloads, file previews, authentication, thumbnails, archive operations, recursive search, and host device mounting are intentionally excluded from the current release.

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
                      ├─ local filesystem browser
                      └─ controlled rename/upload executors
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
- `readOnly: true` blocks mutation in application policy and is shown as `RO`; `readOnly: false` opts the source into controlled writes and is shown as `RW`.
- `RW` is not proof of filesystem access. The API process/container user must also have write permission, and a Docker bind mount must be writable.
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

The checked-in `config/sources.json` and `compose.yaml` intentionally keep every configured source read-only. To opt in one narrowly scoped source, make both changes in deployment-specific files:

```json
{
  "id": "rename-lab",
  "name": "Rename Lab",
  "path": "/sources/rename-lab",
  "enabled": true,
  "readOnly": false,
  "defaultLeft": false,
  "defaultRight": false
}
```

```yaml
services:
  reachcommander:
    volumes:
      - ./config:/config:ro
      - /srv/rename-lab:/sources/rename-lab:rw
```

The host directory must be writable by UID/GID `1000:1000`. Never mount `/`, a home directory containing unrelated data, or `/var/run/docker.sock`. Keep backups for any source enabled for writes.

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

## Active-panel toolbar and search

The toolbar on the left side of the top bar always reflects the active panel, source, and logical directory; hardware monitoring remains on the right. Opening Multi-Rename or Add files captures that context, so switching panels cannot redirect an operation already under review.

Search filters only the loaded current directory and preserves a separate value per panel:

- A value without wildcards is a case-insensitive substring search: `invoice` matches `old-invoice.pdf`.
- `*` matches zero or more characters and `?` matches exactly one character against the complete entry name: `*.exe`, `report-??.pdf`, and `photo*`.
- Every other character is literal. For example, `a+b[1].txt` does not use regular-expression semantics.
- `Ctrl+F` focuses the active search field. Typing while a panel has focus appends to its search; Backspace removes one character and Escape clears it.

Source chips show `RO` for application read-only policy and `RW` for application write opt-in. Unavailable sources remain visible with an accessible explanation. Multi-Rename and Add files require an available `RW` source, and the server still revalidates source policy, containment, symlinks, staleness, and actual storage permissions.

## Multi-Rename

The Total Commander-inspired Multi-Rename Tool opens from the toolbar or `Ctrl+M`. It uses selected files and directories in visible selection order; with no selection, the non-parent cursor row is used. Entries must be direct children of one active directory, symbolic links are rejected, and one preview is limited to 5,000 entries.

The complete behavior and safety contract is recorded in the [Multi-Rename design](docs/superpowers/specs/2026-08-19-reachcommander-multi-rename-design.md).

- `[N]` inserts the original basename, `[E]` the original extension, and `[C]` the configured counter. Ranges such as `[N1-3]` and `[E1-3]` select one-based inclusive character ranges.
- Search/replace supports literal or bounded regular-expression matching, optional case sensitivity, and optional extension replacement.
- Case modes are unchanged, lowercase, uppercase, capitalize words, and sentence case. Counter start, step, and digit padding are configurable.
- The live server-authoritative preview shows every complete new filename and marks unchanged, conflict, invalid, and stale rows. Start remains disabled unless the complete batch is valid and changes at least one entry.

Execution revalidates the source, direct-child paths, fingerprints, conflicts, and write access. A two-phase temporary-name algorithm handles swaps, cycles, and case-only changes. Handled failures attempt to compensate the complete batch; if compensation is incomplete, the UI reports logical recovery locations and requires acknowledgement. Repeating Execute or Undo for the same identifier is idempotent.

One-level Undo is available for 30 minutes in the current API process and revalidates the filesystem before reversing the batch. Preview plans expire after 10 minutes. An API restart, process crash, external filesystem mutation, or power loss can invalidate previews/Undo and may require administrator recovery; this is not a substitute for backups. Date/time tokens, presets, plugins, persistent history, recursive rename, and F4 single-item rename are not included.

## Add files

Add files accepts multiple browser-selected files into the captured active directory. The review dialog shows the immutable destination, files, total size, and server-supplied limits before starting. Production defaults are 10 GiB per file, 50 GiB per batch, 100 files per batch, and two concurrent batches; override them with `Uploads__MaxFileBytes`, `Uploads__MaxBatchBytes`, `Uploads__MaxFilesPerBatch`, and `Uploads__MaxConcurrentBatches`.

The complete behavior and safety contract is recorded in the [active-panel operations design](docs/superpowers/specs/2026-08-20-reachcommander-active-panel-toolbar-design.md).

Uploads stream multipart bodies and stage files beside their destination. Arbitrary file types are accepted as inert data—ReachCommander never executes uploaded content. Any invalid name, size/count violation, duplicate, or existing destination name rejects the entire batch; there is no overwrite, auto-rename, skip, merge, or partial-success mode. Handled failures remove staged and finalized additions before returning. An abrupt process/host failure can leave reserved `.reachcommander-upload-*.partial` files for an administrator to inspect and remove after confirming no upload process is active.

The browser shows progress and permits cancellation while bytes are being sent. Finalization is intentionally non-cancellable. Folder upload, drag-and-drop, clipboard/URL import, resume, archive extraction, and a background transfer queue are not included.

## Keyboard and pointer controls

| Input | Action |
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
| `Ctrl+F` | Focus the active-panel search |
| `Ctrl+L` | Focus the active pane's editable logical path |
| `Ctrl+M` | Open Multi-Rename for the active selection or cursor row |
| `Ctrl+R` | Refresh the active directory |
| `Ctrl+T` | Create a tab at the active path |
| `Ctrl+W` | Close the active tab; the last tab is replaced with a root tab |
| Type while a pane has focus | Append to its active-panel search |
| `F9` | Toggle the command reference menu |
| `F3`–`F8` | Reserved/disabled until Milestone 2 |

Pointer selection supports click, `Ctrl+click`, and `Shift+click`. Source buttons, toolbar actions, tabs, sortable headings, the path field, search, and F9 also work with pointer/touch input.

## API

```text
GET /api/sources
GET /api/files?sourceId=media&path=/Movies
GET /api/files/info?sourceId=media&path=/Movies/Gladiator%20II.mkv
GET /api/system-metrics
GET /api/uploads/limits
POST /api/uploads?sourceId=downloads&path=/Incoming
POST /api/batch-renames/preview
POST /api/batch-renames/{planId}/execute
POST /api/batch-renames/{operationId}/undo
GET /health
```

API errors use `application/problem+json` and stable codes. Common path/source codes are `invalid_path`, `path_forbidden`, `source_not_found`, `entry_not_found`, `source_read_only`, and `source_unavailable`. Rename adds `invalid_rename_rule`, `batch_too_large`, `rename_plan_not_found`, `rename_plan_expired`, `rename_plan_stale`, and `rename_recovery_required`. Upload adds `upload_empty`, `upload_name_invalid`, `upload_name_conflict`, `upload_file_too_large`, `upload_batch_too_large`, `upload_too_many_files`, `upload_unsupported_media_type`, `upload_malformed`, `upload_storage_unavailable`, and `upload_cleanup_required`. Unknown `/api/*` routes remain JSON 404 responses; client-side routes fall back to the Angular application.

## Security model

- The browser sends a configured source ID and normalized logical path only. Source roots and resolved physical paths are never included in DTOs, client state, or Problem Details.
- The backend rejects NUL characters, relative traversal, rooted paths, drive-qualified paths, and UNC paths.
- Every request is resolved beneath its configured source. Existing path components are canonicalized one by one, symbolic links are resolved, and containment is checked after each resolution and on the final path.
- A symlink that escapes its source is rejected even if the final target exists.
- Only Multi-Rename and Add files expose write paths. Both require explicit `readOnly: false`, re-resolve logical paths beneath a configured source, reject symbolic-link targets, and serialize overlapping directory mutations.
- The checked-in sample bind mounts remain read-only, providing defense in depth until an administrator opts a narrow source into writes at both configuration and filesystem layers.
- Filesystem exceptions are converted to stable, non-leaking Problem Details. Automated tests use temporary fixture trees, never developer data.

This boundary protects the configured filesystem roots; it is not a substitute for authentication, host permissions, network isolation, TLS, backups, malware scanning, or container hardening. ReachCommander still has no built-in authentication, so place it behind an authenticated HTTPS reverse proxy and do not expose it publicly.

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

Playwright uses fresh temporary writable Downloads plus read-only Media/Archive roots and the real published application. It never points at personal or production folders:

```powershell
Push-Location tests/e2e
npm ci
npm run install:browsers
npm test
Pop-Location
```

Failed Playwright runs retain screenshots, traces, and an HTML report under `artifacts/`.

## Roadmap

- **Milestone 2A — controlled active-panel operations (current):** Total Commander-inspired Multi-Rename, complete authoritative preview, one-level Undo, wildcard search, and bounded all-or-nothing uploads.
- **Milestone 2B — remaining safe mutations:** confirmation dialogs and secure F4 single rename, F5 copy, F6 move, F7 create directory, and F8 delete endpoints.
- **Milestone 3 — resilient transfers:** queued jobs, `BackgroundService`, channels, SignalR progress, throughput/ETA, cancellation, failure recovery, and verified copy-then-delete cross-device moves.
- **Milestone 4 — access control:** local users, password authentication, secure sessions, per-source permissions, read-only roles, settings, and feature flags.
- **Milestone 5 — richer file workflows:** image/video/PDF/text previews, thumbnail mode, recursive search, downloads, archives, bookmarks, history, and favorites.

The recommended next step is Milestone 2B as a separate security-reviewed slice: define each mutation's authorization/read-only invariants first, add temporary-filesystem tests, implement confirmation UX, then expose the function keys one operation at a time.
