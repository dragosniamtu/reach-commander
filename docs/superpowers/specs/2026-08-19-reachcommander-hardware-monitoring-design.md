# ReachCommander Live Hardware Monitoring Design

## Summary

ReachCommander will display live host-health information in a compact widget at the top-right of the application. The widget expands into a detailed current-state panel for CPU, memory, configured-source storage, GPUs, fans, network throughput, and host uptime.

Collection remains part of the ReachCommander process; there is no separate host agent. Native Windows development uses a Windows collector. Ubuntu production uses Linux collectors inside the existing container with explicit read-only host telemetry mounts and optional GPU device exposure. Missing sensors degrade independently to unavailable values.

The feature is informational and read-only. It never changes fan curves, thermal limits, power settings, GPU settings, or other host state.

## Goals

- Show a compact five-second host-health summary in the application top-right.
- Provide detailed current CPU, RAM, storage, GPU, fan, network, and uptime readings on demand.
- Support native Windows development and native or Dockerized Ubuntu production.
- Auto-detect NVIDIA, AMD, and Intel GPUs and return the metrics each driver exposes.
- Keep hardware collection isolated from browser request volume through a background sampler and immutable cache.
- Keep healthy metrics visible when another device, sensor, or collector is unavailable.
- Preserve the existing locked-down container posture: no privileged mode, Docker socket, writable sysfs, or hardware-control APIs.
- Avoid exposing physical paths, serial numbers, raw driver errors, process information, or unbounded device labels.

## Non-Goals

- Historical charts, time-series storage, or long-term retention.
- Alerts, notifications, email, webhooks, or external monitoring export.
- Per-process CPU, memory, network, or GPU usage.
- SMART diagnostics, disk health, battery health, voltages, clocks, or power consumption.
- Fan, thermal, power, clock, or GPU control.
- Guaranteed availability of temperature, fan, or GPU readings on every system.
- Reading the physical Windows host from a Linux container under Docker Desktop.
- Installing or communicating with a separate ReachCommander host agent.

## Approved Product Decisions

- The metrics describe the server host running ReachCommander, not the browser computer.
- Windows and Linux are supported through separate platform collectors.
- Windows development runs ReachCommander natively for physical host sensors.
- Ubuntu production may run in Docker with explicit read-only telemetry mounts.
- The widget lives in the application top bar, not inside or duplicated across file panes.
- Sampling and browser refresh occur every five seconds.
- The first version includes CPU usage and temperature, RAM, configured-source storage, GPU usage/memory/temperature, fan RPM, network throughput, and host uptime.
- NVIDIA, AMD, and Intel are auto-detected on a best-effort basis.
- Unsupported or inaccessible sensor values display as unavailable while other metrics continue updating.

## Architecture

```text
Windows collector ─┐
Linux collectors ──┼─> HardwareMetricsSampler ─> immutable cached snapshot
GPU collectors ────┘                                  │
                                                     ▼
                                      GET /api/system-metrics
                                                     │
                                                     ▼
                                        SystemMetricsStore
                                                     │
                          compact top-bar widget + details panel
```

### Application boundary

The application layer defines `IHardwareMetricsSnapshotProvider` and platform-neutral immutable records. It does not reference LibreHardwareMonitor, sysfs, procfs, native GPU libraries, ASP.NET Core, or Angular.

The public snapshot contains:

- `sampledAt` and `hostUptimeSeconds`;
- overall `healthy`, `partial`, `stale`, or `disabled` state;
- CPU aggregate utilization, package temperature, and optional warning/critical thresholds;
- physical memory used, available, and total bytes;
- configured-source storage readings keyed by safe source ID and display name;
- zero or more GPU readings;
- zero or more fan readings;
- aggregate network receive and transmit bytes per second;
- stable collector availability codes suitable for user-facing explanations.

Every optional measurement is nullable. Zero is a valid reading and is never used to mean unavailable.

### Infrastructure collectors

Collectors implement a small asynchronous interface and return their own partial contribution plus stable diagnostics. A collector cannot mutate the combined snapshot directly.

`HardwareMetricsSampler` is a singleton hosted service. It samples immediately at startup, then every five seconds using `TimeProvider`. It invokes collectors through independent exception and timeout boundaries, normalizes their results, and atomically replaces the cached immutable snapshot. `GET /api/system-metrics` only reads that cache, so browser count does not multiply hardware probes.

A snapshot is:

- `healthy` when required base collectors and all collectors applicable to detected hardware completed; the absence of a sensor family or GPU is not itself a failure;
- `partial` when base data exists but an applicable enabled collector timed out, failed, or lacked required access;
- `stale` when the most recent usable snapshot is older than 15 seconds;
- `disabled` when monitoring is disabled by configuration.

The service preserves the last usable values during a transient failure and replaces them after collection recovers. The snapshot provider derives the effective stale state from `sampledAt` and `TimeProvider` on every API read, so data becomes stale even if the sampler stops entirely.

### Windows collector

The Windows collector activates only when `OperatingSystem.IsWindows()` is true and uses `LibreHardwareMonitorLib` behind an adapter owned by Infrastructure. It enables CPU, memory, motherboard, controller, network, and GPU discovery, updates the hardware tree once per sample, and maps only the approved sensor types. Configured-source capacity remains the responsibility of the shared storage collector.

The adapter owns and disposes the LibreHardwareMonitor computer instance. Tests consume an internal sensor-tree abstraction instead of requiring real hardware. Some motherboard and fan sensors require an elevated Windows process; lack of elevation produces partial results rather than startup failure.

Windows-native development is the supported path for Windows host telemetry. Docker Desktop continues to run the existing Linux image inside a Linux VM and cannot provide complete physical Windows host readings without an agent, which is outside this design.

### Linux base collectors

Linux collectors use fixed configured roots and read-only files:

- `/proc/stat` sampled as deltas for aggregate CPU utilization;
- `/proc/meminfo` for physical memory;
- `/proc/uptime` for host uptime;
- `/proc/net/dev` sampled as deltas for aggregate non-loopback receive/transmit rates;
- `/sys/class/hwmon` and `/sys/class/thermal` for labeled temperatures and fan RPM;
- existing configured source roots for live storage capacity.

The container deployment remaps host telemetry beneath fixed paths such as `/host/proc` and `/host/sys`. Those roots come only from trusted server configuration and are never accepted from HTTP input.

Linux hwmon values are parsed according to the kernel ABI, including fixed-point temperature units and optional label, maximum, critical, alarm, and fault fields. Faulted readings are unavailable. Duplicate readings exposed through more than one sysfs view are deduplicated by canonical device identity inside Infrastructure; no canonical path is returned publicly.

### GPU collectors

GPU collectors first enumerate safe adapter identities, assign a snapshot-local ordinal ID, and expose only a sanitized vendor and display name.

- NVIDIA uses dynamically detected NVML when its driver library and device are exposed. It reads utilization, framebuffer memory, and supported temperatures.
- AMD reads documented DRM/hwmon attributes and common driver attributes for utilization and VRAM when present.
- Intel reads stable DRM/hwmon attributes that the installed kernel driver exposes. Metrics requiring XPU-SMI, root, `SYS_ADMIN`, or writable device access remain unavailable in the default hardened container.

No collector constructs a shell command from input. Missing libraries, inaccessible devices, unsupported calls, parse errors, or a single failed adapter are isolated and represented with stable availability codes.

### Metric normalization

- Utilization values are decimal percentages clamped to `0..100` after rejecting non-finite input.
- Temperatures are decimal degrees Celsius.
- Fan speeds are non-negative revolutions per minute.
- Memory, storage, and network counters use bytes; the UI formats binary storage/memory units and rates per second.
- Network throughput aggregates active non-loopback interfaces and rejects negative deltas caused by counter reset or interface replacement.
- Storage contains one reading per enabled ReachCommander source. Duplicate sources on the same underlying volume may show the same capacity; the API does not reveal the volume path or identifier.
- Device and sensor labels are trimmed, control-character-free, and bounded to 96 characters.

## API

The feature adds one read-only endpoint:

```text
GET /api/system-metrics
```

The response is the latest cached snapshot. It contains only normalized values, safe labels, source IDs, timestamps, state, and stable availability codes. It never contains:

- configured source roots or resolved physical paths;
- procfs/sysfs paths;
- PCI addresses, device serial numbers, or hostnames;
- process identifiers or process names;
- arbitrary exception text, command output, or native-library errors.

When monitoring is disabled, the endpoint returns a successful `disabled` snapshot. Before the first collection completes, it returns `503 application/problem+json` with `code: metrics_not_ready`. Unexpected API failures use the existing safe Problem Details pipeline.

The endpoint is same-origin and unauthenticated like the existing application. Documentation repeats the trusted-network warning because operational telemetry can reveal server activity.

## Angular State and Polling

`SystemMetricsStore` is independent of both pane states and Multi-Rename state. It owns:

- the latest immutable snapshot;
- initial loading, refresh pending, stale, disabled, and safe error states;
- a five-second polling timer;
- a monotonically increasing request token so a late response cannot replace newer data;
- immediate refresh when a hidden browser tab becomes visible again.

The store starts with application initialization. It does not clear the previous snapshot on a transient request failure. It stops timers on destruction and does not overlap requests. Browser polling never changes the server sampling cadence.

## User Interface

### Compact top-bar widget

The widget occupies the far-right side of the existing application top bar and shows:

```text
CPU 18% · 54°C | RAM 43% | STORAGE 71% | GPU 12%
```

- CPU temperature is omitted from the compact line when unavailable.
- Storage represents the fullest available configured source.
- GPU represents the busiest detected adapter.
- An unavailable compact value renders as an em dash without hiding adjacent values.
- The widget exposes overall healthy, partial, stale, or disabled state with text/iconography as well as color.

The whole widget is a button with `aria-expanded` and an accessible summary. It opens a right-aligned details panel and closes through Escape, its close button, or an outside click. Focus moves into the panel when opened and returns to the widget when closed.

Below the responsive breakpoint, the compact line collapses to a single `System` indicator and state icon. The expanded details panel remains an overlay that fits within the viewport.

### Details panel

The details panel groups current readings as:

1. Overall state, snapshot age, and host uptime.
2. CPU utilization and package temperature.
3. memory used, available, total, and percentage.
4. configured sources with used, free, total, and percentage.
5. each GPU with vendor/name, utilization, memory, and temperature.
6. each detected fan with label and RPM.
7. aggregate network receive and transmit rates.
8. concise explanations for unavailable collector families.

The first version has no chart, sparkline, history selector, alert configuration, or raw sensor tree.

### Status presentation

- RAM, storage, CPU, and GPU utilization use normal below 80%, warning from 80% through 94.99%, and critical from 95%.
- Temperatures use reported warning/maximum/critical thresholds when the underlying sensor supplies them. Without a trustworthy threshold, temperature remains informational.
- Kernel/native alarm or fault flags override percentage styling for that sensor.
- Unavailable is neutral, not an error color.
- A snapshot older than 15 seconds is visibly labeled `STALE`.

Five-second numeric updates do not use a live region and therefore do not repeatedly interrupt assistive technology. Only transitions among healthy, partial, stale, recovered, warning, and critical states are announced through a polite live region. Status never relies on color alone.

## Deployment

### Windows development

Run ReachCommander natively with `dotnet run` or the production published executable. The Windows collector activates automatically. Administrator rights are optional: the application remains functional with partial metrics when restricted sensors cannot be read.

Running the Linux container through Docker Desktop reports the Linux VM/container environment rather than complete Windows physical-host sensors. Documentation makes that distinction explicit.

### Ubuntu native

Native execution uses `/proc` and `/sys` directly with the service account's existing read permissions. GPU metrics require installed host drivers and access to the relevant device/library interfaces.

### Ubuntu Docker

The default Compose file remains hardened and usable. A documented opt-in `compose.hardware.yaml` override adds only the required read-only telemetry mounts and optional devices. The override may expose:

- host `/proc/stat`, `/proc/meminfo`, `/proc/uptime`, and `/proc/net/dev` as individual read-only mounts;
- the required host sysfs hardware/thermal/DRM views beneath `/host/sys`, read-only;
- `/dev/dri` plus the configured render-group ID for AMD/Intel;
- NVIDIA devices and driver libraries through the NVIDIA Container Toolkit.

The hardware override does not add `privileged: true`, the Docker socket, `/dev/mem`, writable host filesystems, writable sysfs, or `SYS_ADMIN`. Intel metrics that require elevated capabilities remain unavailable. Existing filesystem source mounts retain their independently configured read-only/read-write policy.

## Configuration

`HardwareMetrics` configuration includes:

- `Enabled`, default `true`;
- `SampleIntervalSeconds`, default and minimum `5` for this release;
- `StaleAfterSeconds`, default `15` and required to exceed the sample interval;
- fixed Linux proc/sys roots for native versus container deployment;
- per-family enable switches for temperatures, fans, network, and GPUs;
- a bounded collector timeout shorter than the sample interval.

Configuration is validated at startup. Invalid intervals or non-absolute Linux roots fail startup with a safe configuration error. Browser clients cannot change collector roots, enable devices, or alter the sampling rate.

## Error Handling and Observability

Each collector records success, unavailable, timeout, unsupported, or failed status. Logs include the collector family, platform, elapsed collection time, stable code, and snapshot correlation ID. They exclude physical paths, raw proc/sys contents, serial numbers, and arbitrary exception messages at normal log levels.

One collector's failure never cancels healthy collectors. The hosted service catches expected I/O, permissions, parsing, missing-library, and timeout failures. It does not catch process-fatal exceptions. Shutdown stops scheduling, cancels cooperative work, and disposes Windows/native GPU handles after their outstanding call returns without delaying host shutdown beyond the configured shutdown budget.

Metrics collection itself is bounded: files have strict maximum read sizes, line and sensor counts are capped, labels are bounded, and results are normalized before caching. Each collector has one in-flight operation and a two-second aggregation deadline. Cooperative managed collectors observe cancellation. A synchronous native-library call cannot be forcibly aborted safely; if it exceeds the deadline, aggregation marks that collector timed out, never starts a second overlapping call, keeps serving the cached snapshot, discards the late result, and allows a new invocation only after the timed-out call has returned.

## Testing Strategy

### Backend unit tests

- CPU delta parsing, counter reset, and zero-total handling from proc fixtures.
- memory, uptime, and network parsing with malformed/oversized fixture rejection.
- hwmon temperature/fan units, labels, thresholds, alarm/fault flags, symlinked layout, and deduplication.
- source-storage mapping without physical-path fields.
- Windows sensor-tree mapping through fake LibreHardwareMonitor adapter nodes.
- NVIDIA, AMD, and Intel detection/mapping through fake native/sysfs boundaries.
- missing libraries, inaccessible devices, timeouts, parse failures, and partial aggregation.
- five-second cadence, non-overlap, 15-second staleness, last-good preservation, recovery, cancellation, and disposal with fake `TimeProvider`.
- label sanitization, count limits, finite numeric validation, and range clamping.

Tests use repository fixtures and temporary directories only. They do not depend on developer hardware, admin rights, `/proc`, `/sys`, or a GPU.

### API integration tests

- ready, partial, stale, disabled, and not-ready responses.
- camel-case enums and JSON content type.
- fixed normalized CPU, RAM, storage, GPU, fan, network, and uptime values through a fake snapshot provider.
- no configured roots, sysfs/procfs paths, PCI addresses, serial numbers, process data, or raw failures in successful or failed responses.
- unknown API routes remain JSON 404 responses.

### Angular tests

- five-second polling with fake timers and no overlapping requests.
- late-response suppression, visibility resume, stale transition, and last-good preservation.
- compact CPU/RAM/storage/GPU selection and unavailable-value formatting.
- details groups, multiple GPUs/sources/fans, byte/rate formatting, and snapshot age.
- warning/critical thresholds, sensor-provided temperature thresholds, and non-color status text.
- keyboard open/close, focus restoration, `aria-expanded`, reduced viewport layout, and restrained announcements.

### Playwright and deployment checks

- top-right widget is present, opens, closes, and exposes current basic readings without assuming exact host values.
- details panel remains within desktop and narrow viewport bounds.
- native Windows startup tolerates unavailable privileged sensors.
- default Compose starts with partial/available metrics as its environment permits.
- Ubuntu hardware override validates without privileged mode or Docker socket.
- optional AMD/Intel `/dev/dri` and NVIDIA configurations are documented and configuration-validated.
- file browsing, keyboard navigation, and Multi-Rename behavior remain unaffected.

## Acceptance Criteria

- The top-right widget refreshes every five seconds and opens an accessible details panel.
- CPU, RAM, configured-source storage, uptime, and network readings work on supported native Windows and Ubuntu environments.
- Ubuntu Docker obtains host readings when the documented read-only telemetry override is enabled.
- NVIDIA, AMD, and Intel adapters auto-detect and expose only available metrics.
- Missing temperatures, fans, GPUs, drivers, privileges, or mounts do not break the endpoint or UI.
- The browser receives no host paths, serial numbers, PCI identifiers, process information, or raw hardware errors.
- No collector offers hardware-control behavior or writes through procfs, sysfs, GPU, or fan interfaces.
- Browser count does not increase the server's five-second collection frequency.
- Stale data is identified after 15 seconds and recovery is visible without discarding other healthy readings.
- Default container hardening remains intact; richer telemetry is an explicit deployment opt-in.

## Future Extensions

Later reviewed slices may add historical charts, configurable alerts, OpenTelemetry/Prometheus export, SMART health, power draw, per-interface networking, or a separate host agent for remote/Windows-container deployments. Hardware control and per-process surveillance remain outside the intended ReachCommander product scope unless separately justified and threat-modeled.

## Primary References

- [LibreHardwareMonitor integration and privilege notes](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/README.md)
- [Linux kernel hwmon sysfs interface](https://docs.kernel.org/hwmon/sysfs-interface.html)
- [Linux thermal sysfs interface](https://docs.kernel.org/driver-api/thermal/sysfs-api.html)
- [NVIDIA NVML utilization API](https://docs.nvidia.com/deploy/nvml-api/structnvmlUtilization__t.html)
- [AMD GPU hwmon interfaces](https://docs.kernel.org/gpu/amdgpu/thermal.html)
- [Intel XPU Manager capabilities and container constraints](https://github.com/intel/xpumanager)
