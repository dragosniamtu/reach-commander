# Media Preview Host Safety Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent browser-compatible media previews from monopolizing a self-hosted machine while retaining useful playback and observable cancellation.

**Architecture:** Keep the existing in-process single preview worker. Apply a validated two-thread `ultrafast` profile and best-effort lower process priority at the FFmpeg boundary, then apply an installer-managed Compose CPU ceiling as defense in depth.

**Tech Stack:** .NET 10, `System.Diagnostics.Process`, xUnit, Docker Compose, Python renderer tests, Bash installer contracts.

## Global Constraints

- Work directly on `master`; do not create a worktree.
- Preserve shell-free FFmpeg invocation, the single bounded worker, authentication, antiforgery, rate limiting, and path containment.
- Default `MaximumTranscodeThreads` to `2` and validate `1..8`.
- Default `TranscodePreset` to `ultrafast`; accept only `ultrafast`, `superfast`, or `veryfast`.
- Do not add a memory limit; production evidence showed no memory pressure.
- Existing image-only updates receive runtime safeguards; existing installer-managed deployments require an installer refresh for the Compose CPU ceiling.
- Do not stage or modify the unrelated `NC-theme.png` file.

---

### Task 1: Validated FFmpeg preview profile

**Files:**
- Modify: `tests/ReachCommander.UnitTests/MediaPreviews/MediaProcessRunnerTests.cs`
- Modify: `tests/ReachCommander.UnitTests/MediaPreviews/MediaPreviewServiceTests.cs`
- Modify: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewOptions.cs`
- Modify: `src/ReachCommander.Infrastructure/MediaPreviews/MediaPreviewOptionsValidator.cs`
- Modify: `src/ReachCommander.Infrastructure/MediaPreviews/MediaTranscodeRunner.cs`

**Interfaces:**
- Produces: `MediaPreviewOptions.MaximumTranscodeThreads : int` and `MediaPreviewOptions.TranscodePreset : string`.
- Produces: `MediaTranscodeRunner.CreateStartInfo(string executable, string inputPhysicalPath, string outputDirectory, int maximumThreads, string preset)`.

- [ ] **Step 1: Write failing option and argument tests**

Add assertions that defaults are `2` and `ultrafast`, validator rejects thread counts `0` and `9` plus an unrecognized preset, and `CreateStartInfo` contains one input-side and one output-side `-threads 2` plus `-preset ultrafast` without shell evaluation.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --no-restore --filter "FullyQualifiedName~MediaProcessRunnerTests|FullyQualifiedName~MediaPreviewOptions"
```

Expected: compilation/assertion failure because the properties and four-argument `CreateStartInfo` overload do not exist.

- [ ] **Step 3: Implement the minimum validated profile**

Add the two options, validate their exact ranges/allowlist, pass them into `CreateStartInfo`, and emit FFmpeg arguments in this order:

```text
-nostdin -hide_banner -loglevel warning
-threads 2 -i <input>
-map 0:v:0 -map 0:a:0?
-c:v libx264 -preset ultrafast -threads 2 -pix_fmt yuv420p
```

Retain all existing audio and HLS arguments unchanged.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the command from Step 2. Expected: all selected tests pass.

### Task 2: Lower-priority process and resource-profile logging

**Files:**
- Modify: `tests/ReachCommander.UnitTests/MediaPreviews/MediaProcessRunnerTests.cs`
- Modify: `src/ReachCommander.Infrastructure/MediaPreviews/MediaProbeRunner.cs`
- Modify: `src/ReachCommander.Infrastructure/MediaPreviews/MediaTranscodeRunner.cs`

**Interfaces:**
- Produces: `MediaProcessExecution.TrySetBelowNormalPriority(Process process) : bool`.
- Consumes: the validated options from Task 1.

- [ ] **Step 1: Write failing priority and log tests**

Start a bounded child process, call `TrySetBelowNormalPriority`, and assert it returns true on Windows or Linux without terminating the child. Capture runner logs from a short fake executable/process fixture and assert the startup event contains the configured thread count, preset, and `PriorityLowered` field but no physical path.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --no-restore --filter FullyQualifiedName~MediaProcessRunnerTests
```

Expected: compilation failure because `TrySetBelowNormalPriority` is absent.

- [ ] **Step 3: Implement best-effort priority lowering**

After `Process.Start()`, set `process.PriorityClass = ProcessPriorityClass.BelowNormal` inside a helper that catches only process/OS capability exceptions and returns false. Log the resource profile using structured fields:

```text
FFmpeg process {ProcessId} started for media preview {SessionId}, file {VideoName}, with {MaximumThreads} threads, preset {Preset}, lower priority applied {PriorityLowered}.
```

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the command from Step 2. Expected: all media process tests pass.

### Task 3: Strict deployment CPU-limit contract

**Files:**
- Modify: `tests/installer/test_render_config.py`
- Modify: `deploy/render_config.py`
- Modify: `deploy/compose.release.yaml`
- Modify: `compose.yaml`
- Modify: `tests/installer/workflow-contract.test.mjs`

**Interfaces:**
- Produces: deployment request field `cpuLimit`, `DeploymentRequest.cpu_limit : str`, and `.env` key `REACHCOMMANDER_CPU_LIMIT`.
- Produces: Compose property `cpus: "${REACHCOMMANDER_CPU_LIMIT}"`.
- CPU-limit values are normalized finite decimals in the inclusive range `0.25..64`.

- [ ] **Step 1: Write failing renderer and Compose contract tests**

Extend the canonical request fixture with `"cpuLimit": "3.0"`. Assert `.env` contains `REACHCOMMANDER_CPU_LIMIT=3.0`, round-trip/load/update helpers preserve it, and values `0`, `65`, `nan`, exponent notation, whitespace, or injected newlines fail. Assert the published Compose template consumes the exact key.

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
python -m unittest tests.installer.test_render_config
node --test tests/installer/workflow-contract.test.mjs
```

Expected: request exact-field validation or missing environment/Compose assertions fail.

- [ ] **Step 3: Implement strict rendering and Compose use**

Add `_validate_cpu_limit` using an ASCII decimal regular expression and `decimal.Decimal`, normalize to a non-exponent decimal string, include it in request serialization, exact `ENV_KEYS`, CLI creation, reconfiguration, source add/remove, and `.env` image-update preservation. Add the Compose `cpus` key and a `3.0` development default.

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the commands from Step 2. Expected: all selected tests pass.

### Task 4: Ubuntu and macOS installer defaults

**Files:**
- Modify: `tests/installer/test_install.sh`
- Modify: `tests/installer/macos/test_install.sh`
- Modify: `tests/installer/macos/test_helpers.sh`
- Modify: `deploy/install.sh`
- Modify: `deploy/macos/install.sh`
- Modify: `deploy/reachcommander`

**Interfaces:**
- Produces: `default_cpu_limit(logical_cpu_count)` with exact mappings `1 -> 0.75`, `2 -> 1.5`, `3 -> 2.0`, and `4+ -> 3.0`.
- Consumes: `--cpu-limit` and `REACHCOMMANDER_CPU_LIMIT` from Task 3.

- [ ] **Step 1: Write failing installer assertions**

Use deterministic test CPU-count overrides. Assert Ubuntu and macOS installations write the expected private `.env` value, reconfiguration preserves/recomputes a valid value through the normal staged transaction, doctor validates the range, and malformed existing values fail closed.

- [ ] **Step 2: Run Bash tests and verify RED where available**

Run on Linux/CI:

```bash
bash tests/installer/test_install.sh
bash tests/installer/test_command.sh
bash tests/installer/macos/test_helpers.sh
bash tests/installer/macos/test_install.sh
```

On Windows without WSL, run the Python/Node executable contracts locally and leave these exact Bash commands for CI.

- [ ] **Step 3: Implement deterministic host defaults**

Detect logical CPUs with `nproc` on Ubuntu and `sysctl -n hw.logicalcpu` on macOS, honoring existing test overrides. Pass the normalized default through staged rendering, store it in `.env`, preserve it across image updates, and validate it in `reachcommander doctor` without exposing host topology in public diagnostics.

- [ ] **Step 4: Re-run available installer tests**

Expected: all locally runnable contracts pass; Linux-only shell tests are explicitly reported as CI-only when Bash is unavailable.

### Task 5: Documentation and complete verification

**Files:**
- Modify: `README.md`
- Modify: `docs/INSTALL.md`
- Modify: `docs/deployment/ubuntu.md`
- Modify: `deploy/README.md`

- [ ] **Step 1: Document the resource boundary**

Document the two-thread `ultrafast` default, best-effort lower priority, installer-derived CPU ceiling, `.env` override range, image-only versus installer-refresh behavior, and the production evidence for not adding a memory limit.

- [ ] **Step 2: Run complete verification**

Run:

```powershell
dotnet test ReachCommander.slnx -c Release --no-restore
Push-Location client/reach-commander-ui; npm test -- --watch=false; npm run build; Pop-Location
node --test tests/installer/release-tags.test.mjs tests/installer/workflow-contract.test.mjs tests/installer/docs-contract.test.mjs
python -m unittest discover -s tests -p "test_*.py"
Push-Location tests/e2e; npm test; Pop-Location
git diff --check
git status --short
```

Expected: all suites pass; `NC-theme.png` remains untouched and untracked.
