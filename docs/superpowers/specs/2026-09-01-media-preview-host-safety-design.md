# Media Preview Host Safety Design

## Problem

An incompatible 1080p x265 video caused ReachCommander's single FFmpeg preview process to create a one-minute load average of 9.03 on an eight-logical-CPU Ubuntu host. Memory remained at 6% usage, swap was unused, disk utilization was 2%, and the previous boot recorded no OOM, storage, watchdog, or kernel-panic event before an unclean hard reset. The existing resilience work cancels closed and abandoned sessions, but an active browser heartbeat can legitimately keep an unrestricted transcode running.

## Considered approaches

1. Limit only FFmpeg. This is portable and keeps the API outside the limit, but a process or codec that does not honor the intended thread budget would still lack a hard container boundary.
2. Limit only the application container. This protects the host, but FFmpeg can consume the complete allowance and make the API sluggish.
3. Move transcoding into a separate worker container. This provides the strongest isolation, but introduces another image, protocol, lifecycle, and deployment boundary that is disproportionate to the current single-user preview feature.

The approved approach layers the first two options and keeps the separate-worker design as a future scalability path.

## Runtime profile

- `MediaPreview:MaximumTranscodeThreads` defaults to `2` and is validated from `1` through `8`.
- `MediaPreview:TranscodePreset` defaults to the fixed low-CPU `ultrafast` profile. Configuration accepts only the bounded presets documented by ReachCommander; arbitrary FFmpeg values are rejected at startup.
- FFmpeg receives the thread ceiling for both input decoding and H.264 output encoding. It continues to run without a shell, with the existing HLS, time, output-size, cancellation, and process-tree boundaries.
- After process start, ReachCommander attempts to lower FFmpeg to `BelowNormal` priority. Failure to lower priority is logged and does not make media preview unavailable because the thread and container ceilings remain enforceable.
- The process-start lifecycle event records the thread count, preset, and whether lower priority was applied, without physical paths or other sensitive data.

## Deployment boundary

Published-image Compose deployments receive a `cpus` ceiling from `REACHCOMMANDER_CPU_LIMIT`. The Ubuntu and macOS installers calculate a conservative default from the available logical CPU count: `0.75` for one CPU, `1.5` for two CPUs, and the smaller of `3.0` or `CPU count - 1` for larger hosts. This leaves scheduler headroom while allowing the API and bounded FFmpeg process to run together.

The value is written to the private installer-managed `.env`, validated as a finite decimal from `0.25` through `64`, preserved through reconfiguration, source-management transactions, updates, backup, and doctor checks, and rendered into Compose. Repository-local development Compose uses a `3.0` default without adding an installer dependency.

An image-only update applies the FFmpeg safeguards. Existing Ubuntu or macOS installations must refresh the installer-managed deployment once to receive the Docker CPU ceiling because the application container cannot rewrite its host Compose configuration.

## Failure behavior

Invalid thread, preset, or CPU-limit configuration fails before startup or Compose activation. Priority lowering is best-effort and observable. The feature does not introduce a memory limit because production evidence showed abundant memory and no swap or OOM pressure. Temperature remains a host responsibility; the existing hardware telemetry remains available to operators.

## Testing

- Unit tests first prove the exact decoder/encoder thread arguments, low-CPU preset, options validation, priority attempt, and structured resource-profile log.
- Renderer and installer tests prove valid CPU limits are generated and preserved, malformed or out-of-range limits fail closed, and Compose consumes the exact private environment value.
- Release workflow contracts prove the published template contains the CPU ceiling.
- Focused tests run red before implementation, followed by the complete .NET, Angular, installer-contract, Python, production-build, and browser acceptance suites available locally.

