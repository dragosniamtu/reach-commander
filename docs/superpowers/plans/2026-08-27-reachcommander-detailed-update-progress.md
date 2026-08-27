# ReachCommander Detailed Update Progress Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a truthful, accessible updater checklist driven by real Ubuntu host stages while preserving safe generic progress with protocol-v1 helpers.

**Architecture:** Extend the restricted updater socket to negotiate protocol v2 with one strict v1 fallback. The fixed host update command emits allow-listed markers, the root helper journals them atomically, ASP.NET Core publishes monotonic same-operation progress, and Angular maps the logical stages into a responsive checklist without receiving raw host output.

**Tech Stack:** Bash, Python 3 standard library, .NET 10 / C#, ASP.NET Core hosted services, xUnit, Angular 22 signals, SCSS, Vitest, Playwright

## Global Constraints

- Work directly on `master`; do not create a branch or worktree.
- Preserve the unrelated untracked `NC-theme.png` and never stage it.
- Protocol-v1 helpers must continue updating with safe generic progress.
- A protocol-v2 helper must return the exact legacy response shape to a v1 request.
- Update progress is observational; marker or progress-persistence failure must not change update, rollback, or exit-code behavior.
- Accept only fixed stage tokens. Never expose raw Docker output, host paths, commands, digests, stack traces, or shell error text through the API or UI.
- Do not mount the Docker socket, add a database, add a streaming transport, or accept browser-selected update targets.
- Do not synthesize percentages, remaining time, download speed, or unreported stages.
- Host-reported progress remains limited to installer-managed Ubuntu deployments.
- Keep the existing six-minute recovery timeout, polling backoff, mutation drain, health check, rollback, and one-time PWA activation behavior.
- The checklist must work in the standard and Norton themes, compact/short PWA viewports, keyboard and screen-reader use, and reduced-motion mode.
- Do not push or create a release tag unless the user explicitly requests it after verification.

## File structure

- `deploy/updater_protocol.py`: protocol-version acceptance and immutable request parsing.
- `deploy/updater_service.py`: v1/v2 response projection, stage transition validation, atomic journal persistence, and streamed marker capture.
- `deploy/reachcommander`: emits fixed progress markers at existing update transaction boundaries.
- `tests/installer/test_updater_protocol.py`: v1/v2 request compatibility and rejection contracts.
- `tests/installer/test_updater_service.py`: response-shape, stage-journal, marker-capture, sanitization, and observational-failure contracts.
- `tests/installer/test_command.sh`: successful, restart-failure, health-failure, and rollback marker-order contracts.
- `src/ReachCommander.Application/SystemUpdates/SystemUpdateModels.cs`: public logical progress enum and status property.
- `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdaterGateway.cs`: protocol-v2-first negotiation, strict v1 fallback, exact schemas, and stage parsing.
- `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateOperationMonitor.cs`: emits validated intermediate same-operation snapshots.
- `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateCoordinator.cs`: monotonic progress publication and restart recovery.
- `src/ReachCommander.Api/Contracts/SystemUpdates/SystemUpdateDtos.cs`: nullable API `progressStage` projection.
- `tests/ReachCommander.UnitTests/SystemUpdates/*`: host-gateway, model, monitor, and coordinator behavior.
- `tests/ReachCommander.IntegrationTests/SystemUpdatesApiTests.cs`: serialized API-stage and sanitization contract.
- `client/reach-commander-ui/src/app/core/api/api.models.ts`: Angular progress-stage union.
- `client/reach-commander-ui/src/app/core/state/system-update.store.ts`: optimistic connecting state and preservation of reported progress.
- `client/reach-commander-ui/src/app/features/system-update/system-update-progress.ts`: pure checklist/view-model mapping.
- `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.*`: progress-list presentation and accessible announcements.
- Angular component/store specs and `tests/e2e/specs/system-update.spec.ts`: UI state and browser acceptance.
- `README.md`, `deploy/README.md`, and `docs/deployment/ubuntu.md`: v1 fallback and helper-refresh guidance.

---

### Task 1: Add the versioned host progress contract and journal state machine

**Files:**
- Modify: `deploy/updater_protocol.py`
- Modify: `deploy/updater_service.py`
- Modify: `tests/installer/test_updater_protocol.py`
- Modify: `tests/installer/test_updater_service.py`

**Interfaces:**
- Consumes: existing fixed `check` and `applyConfiguredChannel` request actions and the protected `system-update.json` journal.
- Produces: `LEGACY_PROTOCOL_VERSION = 1`, `PROTOCOL_VERSION = 2`, `SUPPORTED_PROTOCOL_VERSIONS`, nullable journal key `progressStage`, `AtomicUpdateJournal.advance(...)`, and v1/v2 response projections.

- [ ] **Step 1: Write failing protocol-version tests**

Change the request helper in `test_updater_protocol.py` to accept a version and add explicit dual-version coverage:

```python
def valid_request(
    self,
    action: str = "check",
    protocol_version: int = PROTOCOL_VERSION,
) -> dict[str, object]:
    return {
        "protocolVersion": protocol_version,
        "requestId": str(uuid.uuid4()),
        "action": action,
    }

def test_accepts_legacy_and_detailed_protocols(self) -> None:
    for version in (LEGACY_PROTOCOL_VERSION, PROTOCOL_VERSION):
        with self.subTest(version=version):
            request = UpdaterRequest.parse(
                json.dumps(self.valid_request(protocol_version=version)).encode()
            )
            self.assertEqual(version, request.protocol_version)

def test_rejects_unadvertised_protocol_versions(self) -> None:
    for version in (0, 3, True):
        with self.subTest(version=version):
            with self.assertRaisesRegex(ProtocolError, "incompatible"):
                UpdaterRequest.parse(
                    json.dumps(self.valid_request(protocol_version=version)).encode()
                )
```

Update the constants assertion to require v2 as the newest version and v1 as the only legacy version:

```python
self.assertEqual(1, LEGACY_PROTOCOL_VERSION)
self.assertEqual(2, PROTOCOL_VERSION)
self.assertEqual(frozenset({1, 2}), SUPPORTED_PROTOCOL_VERSIONS)
```

- [ ] **Step 2: Write failing service/journal tests**

Add a request helper argument in `test_updater_service.py` and tests that prove exact response compatibility and legal transitions:

```python
def request(action: str, protocol_version: int = PROTOCOL_VERSION) -> UpdaterRequest:
    return UpdaterRequest.parse(
        json.dumps(
            {
                "protocolVersion": protocol_version,
                "requestId": str(uuid.uuid4()),
                "action": action,
            }
        ).encode()
    )

def test_v1_response_has_exact_legacy_shape_and_v2_adds_progress(self) -> None:
    with tempfile.TemporaryDirectory() as directory:
        runtime = self.runtime(Path(directory))
        legacy = runtime.handle(request("check", LEGACY_PROTOCOL_VERSION))
        detailed = runtime.handle(request("check", PROTOCOL_VERSION))

    self.assertEqual(LEGACY_PROTOCOL_VERSION, legacy["protocolVersion"])
    self.assertNotIn("progressStage", legacy)
    self.assertEqual(PROTOCOL_VERSION, detailed["protocolVersion"])
    self.assertIn("progressStage", detailed)
    self.assertIsNone(detailed["progressStage"])

def test_journal_accepts_forward_and_recovery_transitions(self) -> None:
    with tempfile.TemporaryDirectory() as directory:
        journal = AtomicUpdateJournal(Path(directory) / "system-update.json")
        operation = journal.begin(snapshot(), NOW)
        for stage in (
            "downloading",
            "installing",
            "restarting",
            "healthChecking",
            "restoring",
            "restartingPrevious",
            "verifyingRecovery",
        ):
            operation = journal.advance(operation, stage, NOW)
            self.assertEqual(stage, operation["progressStage"])

def test_journal_ignores_duplicate_unknown_and_backward_progress(self) -> None:
    with tempfile.TemporaryDirectory() as directory:
        journal = AtomicUpdateJournal(Path(directory) / "system-update.json")
        operation = journal.begin(snapshot(), NOW)
        operation = journal.advance(operation, "downloading", NOW)
        operation = journal.advance(operation, "installing", NOW)

        self.assertEqual(operation, journal.advance(operation, "installing", NOW))
        self.assertEqual(operation, journal.advance(operation, "downloading", NOW))
        self.assertEqual(operation, journal.advance(operation, "notAStage", NOW))
```

Also add a legacy-journal migration fixture with `schemaVersion: 1` and no `progressStage`; the new helper must read it as `None` and write the next update using the new schema.

- [ ] **Step 3: Run the host protocol tests and verify RED**

Run:

```powershell
python -m unittest tests/installer/test_updater_protocol.py tests/installer/test_updater_service.py -v
```

Expected: FAIL because v2 constants, `progressStage`, dual response projection, and `AtomicUpdateJournal.advance` do not exist.

- [ ] **Step 4: Implement dual request versions and strict stage validation**

In `updater_protocol.py`, define and use:

```python
LEGACY_PROTOCOL_VERSION = 1
PROTOCOL_VERSION = 2
SUPPORTED_PROTOCOL_VERSIONS = frozenset(
    {LEGACY_PROTOCOL_VERSION, PROTOCOL_VERSION}
)

if (
    not isinstance(protocol_version, int)
    or isinstance(protocol_version, bool)
    or protocol_version not in SUPPORTED_PROTOCOL_VERSIONS
):
    raise ProtocolError(
        "protocol_incompatible",
        "The host updater protocol is incompatible.",
    )

return cls(protocol_version, request_id, action)
```

Export all three constants. Do not permit extra request fields for either version.

In `updater_service.py`, introduce exact schema sets and the finite-state transition map:

```python
LEGACY_JOURNAL_SCHEMA = 1
JOURNAL_SCHEMA = 2
PROGRESS_STAGES = frozenset(
    {
        "downloading",
        "installing",
        "restarting",
        "healthChecking",
        "restoring",
        "restartingPrevious",
        "verifyingRecovery",
    }
)
PROGRESS_TRANSITIONS = {
    None: frozenset({"downloading"}),
    "downloading": frozenset({"installing"}),
    "installing": frozenset({"restarting"}),
    "restarting": frozenset({"healthChecking", "restoring"}),
    "healthChecking": frozenset({"restoring"}),
    "restoring": frozenset({"restartingPrevious"}),
    "restartingPrevious": frozenset({"verifyingRecovery"}),
    "verifyingRecovery": frozenset(),
}
V1_RESPONSE_FIELDS = (
    "supported", "channel", "currentVersion", "targetVersion",
    "currentDigest", "targetDigest", "phase", "reasonCode", "detail",
    "operationId", "lastCheckedAt", "updatedAt",
)
V2_RESPONSE_FIELDS = (*V1_RESPONSE_FIELDS, "progressStage")
```

`AtomicUpdateJournal.begin` must initialize `progressStage` to `None`. Add this method; it deliberately returns unchanged state rather than throwing for bad progress so reporting cannot affect the transaction:

```python
def advance(
    self,
    operation: Mapping[str, object],
    stage: str,
    now: dt.datetime,
) -> dict[str, object]:
    current = operation.get("progressStage")
    if (
        operation.get("phase") != "applying"
        or stage not in PROGRESS_STAGES
        or stage not in PROGRESS_TRANSITIONS.get(current, frozenset())
    ):
        return dict(operation)
    value = {
        **dict(operation),
        "schemaVersion": JOURNAL_SCHEMA,
        "progressStage": stage,
        "updatedAt": _iso_utc(now),
    }
    with self._lock:
        self._write_unlocked(value)
    return value
```

Make journal validation accept schema 1 without `progressStage`, normalize it to `None`, and require schema 2 for writes. Validate that stage is null unless phase is `applying` or terminal. Keep the latest valid stage when `finish` writes a terminal result.

Change response projection to use the request's negotiated version:

```python
def protocol_response(
    request: UpdaterRequest,
    value: Mapping[str, object],
) -> dict[str, object]:
    fields = (
        V1_RESPONSE_FIELDS
        if request.protocol_version == LEGACY_PROTOCOL_VERSION
        else V2_RESPONSE_FIELDS
    )
    response = {
        "protocolVersion": request.protocol_version,
        "requestId": request.request_id,
    }
    response.update({field: value.get(field) for field in fields})
    response["detail"] = _detail_for(str(response.get("reasonCode") or ""))
    return response
```

Use `protocol_response(request, ...)` at every runtime return site. Do not add `progressStage` to a v1 response.

- [ ] **Step 5: Run protocol tests and verify GREEN**

Run:

```powershell
python -m unittest tests/installer/test_updater_protocol.py tests/installer/test_updater_service.py -v
```

Expected: all protocol, journal, socket, discovery, and service tests PASS.

- [ ] **Step 6: Commit the host progress contract**

```powershell
git add deploy/updater_protocol.py deploy/updater_service.py tests/installer/test_updater_protocol.py tests/installer/test_updater_service.py
git commit -m "feat: version updater progress protocol"
```

---

### Task 2: Emit and capture real update transaction stages

**Files:**
- Modify: `deploy/reachcommander`
- Modify: `deploy/updater_service.py`
- Modify: `tests/installer/test_command.sh`
- Modify: `tests/installer/test_updater_service.py`

**Interfaces:**
- Consumes: Task 1 `AtomicUpdateJournal.advance(...)` and the seven fixed `PROGRESS_STAGES` tokens.
- Produces: machine marker `REACHCOMMANDER_UPDATE_STAGE=<token>` on stderr and `SubprocessCommandRunner.run(..., progress_callback=...)`.

- [ ] **Step 1: Add failing shell marker-order contracts**

Add a helper to `test_command.sh`:

```bash
assert_update_stages() {
  local expected="$1"
  local actual
  actual="$(
    printf '%s\n' "$last_output" |
      sed -n 's/^REACHCOMMANDER_UPDATE_STAGE=//p'
  )"
  assert_equal "$expected" "$actual" 'update progress stage order'
}
```

After the existing healthy update assertion, require:

```bash
assert_update_stages $'downloading\ninstalling\nrestarting\nhealthChecking'
```

After the Compose recreation failure assertion, require:

```bash
assert_update_stages $'downloading\ninstalling\nrestarting\nrestoring\nrestartingPrevious\nverifyingRecovery'
```

After the unhealthy-candidate rollback assertion, require:

```bash
assert_update_stages $'downloading\ninstalling\nrestarting\nhealthChecking\nrestoring\nrestartingPrevious\nverifyingRecovery'
```

Also assert that invalid-channel and lock-contention failures emit no stage marker.

- [ ] **Step 2: Add failing streamed-runner and observational-failure tests**

Extend `RecordingRunner.run` with an optional callback and configurable stages:

```python
def run(
    self,
    argv: tuple[str, ...],
    *,
    env: dict[str, str],
    timeout: int,
    shell: bool,
    progress_callback: Callable[[str], None] | None = None,
) -> CommandResult:
    self.argv.append(list(argv))
    self.environments.append(dict(env))
    self.timeouts.append(timeout)
    self.shell_values.append(shell)
    if progress_callback is not None:
        for stage in self.progress_stages:
            progress_callback(stage)
    return CommandResult(self.exit_code, self.output)
```

Add tests with stages `downloading`, `installing`, `restarting`, and `healthChecking`; after the worker finishes, the terminal journal must retain `healthChecking`. Add a fake journal whose `advance` raises `JournalError`; command exit code 0 must still map to `completed`.

Replace the existing `subprocess.run` contract test with a mocked `subprocess.Popen` or a short real child process that emits:

```text
ordinary output
REACHCOMMANDER_UPDATE_STAGE=downloading
REACHCOMMANDER_UPDATE_STAGE=notAStage
REACHCOMMANDER_UPDATE_STAGE=installing
```

Assert that the callback receives only `downloading` and `installing`, command output remains bounded, `shell` is false, and invalid marker contents never enter a journal or public response.

- [ ] **Step 3: Run focused host tests and verify RED**

Run:

```powershell
python -m unittest tests/installer/test_updater_service.py -v
bash tests/installer/test_command.sh
```

Expected: FAIL because the shell emits no markers and the runner accepts no progress callback.

- [ ] **Step 4: Emit fixed markers at the exact shell boundaries**

Add this private helper near the update functions in `deploy/reachcommander`:

```bash
report_update_stage() {
  local stage="${1:-}"
  case "$stage" in
    downloading | installing | restarting | healthChecking | \
      restoring | restartingPrevious | verifyingRecovery)
      printf 'REACHCOMMANDER_UPDATE_STAGE=%s\n' "$stage" >&2
      ;;
    *)
      rc_die 'internal update progress stage is invalid'
      return 1
      ;;
  esac
}
```

In `command_update`, call only literal tokens:

```bash
report_update_stage downloading
if ! resolved_image="$(rc_pull_digest "$requested_channel")"; then
  return 1
fi
```

Call `installing` after image/version validation and before backup/state mutation. Split candidate startup so `restarting` occurs before Compose and `healthChecking` occurs only after Compose succeeds:

```bash
report_update_stage restarting
if rc_compose up -d reachcommander; then
  report_update_stage healthChecking
  if rc_wait_healthy reachcommander 60; then
    rm -f -- "$marker"
    remove_update_backup "$backup_directory"
    printf 'ReachCommander updated successfully to %s (%s).\n' \
      "$resolved_version" "$resolved_image"
    return 0
  fi
fi
```

Before backup restoration emit `restoring`. Split restored startup similarly:

```bash
report_update_stage restartingPrevious
if rc_compose up -d reachcommander; then
  report_update_stage verifyingRecovery
  if rc_wait_healthy reachcommander 60; then
    printf 'ReachCommander: update was unhealthy; the previous deployment was restored.\n' >&2
    return 2
  fi
fi
```

Do not change state-write order, backup boundaries, exit codes, or recovery messages.

- [ ] **Step 5: Stream and validate markers in the host service**

Add:

```python
PROGRESS_MARKER_PREFIX = "REACHCOMMANDER_UPDATE_STAGE="
ProgressCallback = Callable[[str], None]
```

Replace `subprocess.run` in `SubprocessCommandRunner` with `subprocess.Popen` using the same fixed argv, sanitized environment, `stdin=DEVNULL`, `stdout=PIPE`, `stderr=STDOUT`, `text=True`, `shell=False`, and `close_fds=True`. A dedicated reader thread must:

```python
for line in process.stdout:
    candidate = line.rstrip("\r\n")
    if candidate.startswith(PROGRESS_MARKER_PREFIX):
        stage = candidate.removeprefix(PROGRESS_MARKER_PREFIX)
        if progress_callback is not None and stage in PROGRESS_STAGES:
            progress_callback(stage)
        continue
    append_bounded_output(line)
```

The main runner thread waits with the existing timeout, kills the process on `subprocess.TimeoutExpired`, joins the reader, and returns no more than `MAX_COMMAND_OUTPUT_CHARS`. It must never execute through a shell and must never include marker lines in returned output.

In `_apply_worker`, keep the latest immutable operation mapping and swallow only journal progress errors with a fixed log message:

```python
operation_state = dict(operation)

def record_progress(stage: str) -> None:
    nonlocal operation_state
    try:
        operation_state = self._journal.advance(
            operation_state,
            stage,
            self._clock(),
        )
    except JournalError:
        print(
            "ReachCommander updater could not persist update progress.",
            file=sys.stderr,
        )

completed = self._runner.run(
    FIXED_COMMAND,
    env=SANITIZED_ENVIRONMENT,
    timeout=COMMAND_TIMEOUT_SECONDS,
    shell=False,
    progress_callback=record_progress,
)
```

Finish with `operation_state` so the terminal journal retains the last accepted stage. Preserve exit-code mapping `{0: completed, 2: rolledBack, other: failed}`.

- [ ] **Step 6: Run the host lifecycle and lint gate**

Run:

```powershell
python -m unittest tests/installer/test_updater_protocol.py tests/installer/test_updater_service.py -v
bash tests/installer/test_command.sh
shellcheck -x --source-path=SCRIPTDIR deploy/reachcommander tests/installer/test_command.sh
```

Expected: all tests PASS; ShellCheck reports no findings. On a Windows workstation without ShellCheck, record that the CI Ubuntu job remains the authoritative ShellCheck gate, but still run both Python and Bash suites.

- [ ] **Step 7: Commit real host stage capture**

```powershell
git add deploy/reachcommander deploy/updater_service.py tests/installer/test_command.sh tests/installer/test_updater_service.py
git commit -m "feat: report host update stages"
```

---

### Task 3: Negotiate progress in ASP.NET Core and expose the logical API contract

**Files:**
- Modify: `src/ReachCommander.Application/SystemUpdates/SystemUpdateModels.cs`
- Modify: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdaterGateway.cs`
- Modify: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateCoordinator.cs`
- Modify: `src/ReachCommander.Api/Contracts/SystemUpdates/SystemUpdateDtos.cs`
- Modify: `tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateContractTests.cs`
- Modify: `tests/ReachCommander.UnitTests/SystemUpdates/UnixSystemUpdaterGatewayTests.cs`
- Modify: `tests/ReachCommander.IntegrationTests/SystemUpdatesApiTests.cs`
- Modify: `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`

**Interfaces:**
- Consumes: protocol-v2 response key `progressStage` with Task 1 tokens; protocol-v1 response without that key.
- Produces: `SystemUpdateProgressStage`, nullable `SystemUpdateStatus.ProgressStage`, and an API JSON `progressStage` string or null.

- [ ] **Step 1: Write failing application/API contract tests**

Add model coverage:

```csharp
[Fact]
public void Applying_status_serializes_only_the_logical_progress_stage()
{
    var status = SystemUpdateStatusFactory.Applying(
        "stable",
        "v1.3.0",
        "v1.4.0",
        "operation-1",
        Now,
        Now,
        SystemUpdateProgressStage.Downloading);

    var json = JsonSerializer.Serialize(status, JsonOptions);

    Assert.Contains("\"progressStage\":\"downloading\"", json);
    Assert.DoesNotContain("docker", json, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("sha256:", json, StringComparison.OrdinalIgnoreCase);
}
```

In `SystemUpdatesApiTests`, add an applying fixture with `HealthChecking` and assert the API emits `"progressStage":"healthChecking"` while still omitting `/opt/`, `sha256:`, and raw detail.

- [ ] **Step 2: Write failing gateway negotiation and schema tests**

Replace the single-response test transport with a queue transport that records every request. Add:

```csharp
[Fact]
public async Task Gateway_prefers_v2_and_parses_progress()
{
    var transport = new SequenceUpdaterTransport(
        Response(protocolVersion: 2, phase: "applying", progressStage: "downloading"));
    var gateway = new SystemUpdaterGateway(transport, new FixedRequestId());

    var result = await gateway.CheckAsync(default);

    Assert.Equal("downloading", result.ProgressStage);
    Assert.Equal([2], transport.ProtocolVersions);
}

[Fact]
public async Task Gateway_retries_v1_once_for_an_old_helper()
{
    var transport = new SequenceUpdaterTransport(
        LegacyProtocolIncompatibleResponse(),
        Response(protocolVersion: 1, phase: "applying", progressStage: null));
    var gateway = new SystemUpdaterGateway(transport, new FixedRequestId());

    var result = await gateway.CheckAsync(default);

    Assert.Null(result.ProgressStage);
    Assert.Equal([2, 1], transport.ProtocolVersions);
}
```

Also require rejection of unknown stage tokens, a `progressStage` field in v1, a missing `progressStage` field in v2, invalid phase/stage combinations, and malformed v1 fallback responses. A malformed v2 response must not trigger fallback.

- [ ] **Step 3: Run focused backend tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~SystemUpdateContractTests|FullyQualifiedName~UnixSystemUpdaterGatewayTests"
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~SystemUpdatesApiTests
```

Expected: compilation/test failures because the progress enum/property and negotiation are absent.

- [ ] **Step 4: Add the application and DTO progress model**

Add:

```csharp
public enum SystemUpdateProgressStage
{
    Downloading,
    Installing,
    Restarting,
    HealthChecking,
    Restoring,
    RestartingPrevious,
    VerifyingRecovery,
}
```

Add `SystemUpdateProgressStage? ProgressStage` to `SystemUpdateStatus` immediately after `Phase`. Extend `Create` and terminal/applying factory methods. Keep existing call sites source-compatible by placing this optional argument last:

```csharp
public static SystemUpdateStatus Applying(
    string channel,
    string currentVersion,
    string targetVersion,
    string operationId,
    DateTimeOffset? lastCheckedAt,
    DateTimeOffset now,
    SystemUpdateProgressStage? progressStage = null)
```

Use the same final optional parameter for `Completed`, `RolledBack`, and `Failed`; every non-operation factory passes null. Add `ProgressStage` to `SystemUpdateStatusDto` and `FromModel` in the same property order.

- [ ] **Step 5: Implement strict v2-first gateway negotiation**

Add nullable `string? ProgressStage` to `UpdaterSnapshot`. Keep API protocol version 1 unchanged; the internal snapshot records the negotiated host version.

Define exact response fields:

```csharp
private const int LegacyProtocolVersion = 1;
private const int DetailedProtocolVersion = 2;
private static readonly HashSet<string> V1ResponseFields = [
    "protocolVersion", "requestId", "supported", "channel",
    "currentVersion", "targetVersion", "currentDigest", "targetDigest",
    "phase", "reasonCode", "detail", "operationId", "lastCheckedAt", "updatedAt",
];
private static readonly HashSet<string> V2ResponseFields = [.. V1ResponseFields, "progressStage"];
private static readonly HashSet<string> ProgressStages = [
    "downloading", "installing", "restarting", "healthChecking",
    "restoring", "restartingPrevious", "verifyingRecovery",
];
```

`SendAsync` sends v2 first. If and only if the response is an exact legacy protocol-incompatible envelope (`protocolVersion: 1`, null request ID, phase `unavailable`, reason `protocol_incompatible`, exact v1 field set), send one new request with version 1. Parse all other responses against the requested version and fail closed on any mismatch.

Use this request constructor for each attempt:

```csharp
private async Task<string> ExchangeAsync(
    string action,
    int protocolVersion,
    CancellationToken cancellationToken)
{
    var requestId = requestIds.NewId();
    var request = JsonSerializer.Serialize(new
    {
        protocolVersion,
        requestId,
        action,
    }) + "\n";
    return await transport.ExchangeAsync(request, cancellationToken)
        .ConfigureAwait(false);
}
```

The v2 parser requires a nullable logical stage and rejects a stage outside applying/terminal phases. The v1 parser always returns `ProgressStage = null`. Map tokens in `SystemUpdateCoordinator` through an exhaustive switch and pass the resulting enum into applying/terminal factories. Accept only already validated host protocol versions 1 and 2; do not expose the host protocol number as the public API protocol version.

- [ ] **Step 6: Run gateway, model, and API tests**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~SystemUpdateContractTests|FullyQualifiedName~UnixSystemUpdaterGatewayTests"
dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj -c Release --filter FullyQualifiedName~SystemUpdatesApiTests
```

Expected: all selected tests PASS with exact v1/v2 schema validation and sanitized API output.

- [ ] **Step 7: Commit backend protocol negotiation**

```powershell
git add src/ReachCommander.Application/SystemUpdates/SystemUpdateModels.cs src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdaterGateway.cs src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateCoordinator.cs src/ReachCommander.Api/Contracts/SystemUpdates/SystemUpdateDtos.cs tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateContractTests.cs tests/ReachCommander.UnitTests/SystemUpdates/UnixSystemUpdaterGatewayTests.cs tests/ReachCommander.IntegrationTests/SystemUpdatesApiTests.cs tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs
git commit -m "feat: expose trusted update progress"
```

---

### Task 4: Publish monotonic intermediate progress through the coordinator

**Files:**
- Modify: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateOperationMonitor.cs`
- Modify: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateCoordinator.cs`
- Modify: `tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateOperationMonitorTests.cs`
- Modify: `tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateCoordinatorTests.cs`

**Interfaces:**
- Consumes: Task 3 `UpdaterSnapshot.ProgressStage` and `SystemUpdateStatus.ProgressStage`.
- Produces: `WaitForTerminalAsync(UpdaterSnapshot, Action<UpdaterSnapshot>, CancellationToken)` and same-operation monotonic cached progress.

- [ ] **Step 1: Write failing monitor callback tests**

Change the monitor call contract and add:

```csharp
[Fact]
public async Task Publishes_matching_applying_progress_before_terminal_result()
{
    var clock = new AdjustableTimeProvider(StartedAt);
    var installing = Applying("operation-1") with
    {
        ProgressStage = "installing",
        UpdatedAt = StartedAt.AddSeconds(1),
    };
    var gateway = new SequenceGateway(
        installing,
        Terminal("completed", "operation-1"));
    var observed = new List<UpdaterSnapshot>();
    var monitor = CreateMonitor(gateway, clock);

    var result = await monitor.WaitForTerminalAsync(
        Applying("operation-1"),
        observed.Add,
        default);

    Assert.Equal(["installing"], observed.Select(item => item.ProgressStage));
    Assert.Equal("completed", result.TerminalSnapshot!.Phase);
}
```

Add a second test proving applying snapshots for a different operation do not invoke the callback. Update every existing call to pass `_ => { }`.

- [ ] **Step 2: Write failing coordinator monotonicity tests**

Extend `ControlledOperationMonitor` to retain the callback and expose:

```csharp
public void Publish(UpdaterSnapshot snapshot) => _progress!(snapshot);
```

Add a test that starts at downloading, publishes installing, attempts downloading again, attempts another operation ID, publishes restarting and restoring, and then reads the coordinator after each step. Expected public progress is `Downloading`, `Installing`, still `Installing`, still `Installing`, `Restarting`, then `Restoring`.

Add restart recovery coverage where a startup-discovered applying snapshot begins at `HealthChecking`, later advances to `Restoring`, and completes `RolledBack`. Confirm the coordinator never releases a drain it did not acquire.

- [ ] **Step 3: Run monitor/coordinator tests and verify RED**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~SystemUpdateOperationMonitorTests|FullyQualifiedName~SystemUpdateCoordinatorTests"
```

Expected: compilation/test failures because intermediate progress callbacks and monotonic coordinator publication do not exist.

- [ ] **Step 4: Publish validated intermediate snapshots from the monitor**

Change the interface and implementation signature:

```csharp
Task<SystemUpdateMonitorResult> WaitForTerminalAsync(
    UpdaterSnapshot applyingSnapshot,
    Action<UpdaterSnapshot> progress,
    CancellationToken cancellationToken);
```

After existing protocol/support/operation-ID checks, publish applying snapshots and return terminal snapshots:

```csharp
if (snapshot.Phase == "applying")
{
    progress(snapshot);
    continue;
}

if (snapshot.Phase is "completed" or "rolledBack" or "failed")
{
    return new SystemUpdateMonitorResult(snapshot);
}
```

Do not invoke callbacks for unsupported snapshots, operation-ID mismatches, transient exceptions, or terminal states.

- [ ] **Step 5: Add coordinator-side transition defense**

Pass `PublishProgressSnapshot` to the monitor. Under `_stateLock`, require the active monitor task, applying broad phase, and matching operation ID. Map the candidate only after those checks.

Use a stage transition function over application enums:

```csharp
private static bool CanAdvance(
    SystemUpdateProgressStage? current,
    SystemUpdateProgressStage? candidate) => (current, candidate) switch
{
    (null, SystemUpdateProgressStage.Downloading) => true,
    (SystemUpdateProgressStage.Downloading, SystemUpdateProgressStage.Installing) => true,
    (SystemUpdateProgressStage.Installing, SystemUpdateProgressStage.Restarting) => true,
    (SystemUpdateProgressStage.Restarting, SystemUpdateProgressStage.HealthChecking) => true,
    (SystemUpdateProgressStage.Restarting, SystemUpdateProgressStage.Restoring) => true,
    (SystemUpdateProgressStage.HealthChecking, SystemUpdateProgressStage.Restoring) => true,
    (SystemUpdateProgressStage.Restoring, SystemUpdateProgressStage.RestartingPrevious) => true,
    (SystemUpdateProgressStage.RestartingPrevious, SystemUpdateProgressStage.VerifyingRecovery) => true,
    _ => false,
};
```

Repeated/null/backward stages remain ignored. Terminal results still map authoritatively even if their retained last stage is missing. Keep gateway calls and logging outside `_stateLock`.

- [ ] **Step 6: Run focused and complete backend tests**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~SystemUpdateOperationMonitorTests|FullyQualifiedName~SystemUpdateCoordinatorTests"
dotnet test ReachCommander.slnx -c Release
```

Expected: selected tests and the complete backend solution PASS with zero failures.

- [ ] **Step 7: Commit intermediate backend progress**

```powershell
git add src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateOperationMonitor.cs src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateCoordinator.cs tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateOperationMonitorTests.cs tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateCoordinatorTests.cs
git commit -m "feat: publish live update stages"
```

---

### Task 5: Render the accessible updater checklist in Angular

**Files:**
- Modify: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/system-update.store.ts`
- Modify: `client/reach-commander-ui/src/app/core/state/system-update.store.spec.ts`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-progress.ts`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-progress.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.html`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-button.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-dialog.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`

**Interfaces:**
- Consumes: nullable API `progressStage`, broad update phase, operation ID, `updatedAt`, and existing reconnect state.
- Produces: pure `buildSystemUpdateProgress(...)`, semantic standard/recovery lists, client-observed connecting/activation states, and a 30-second stale-progress explanation.

- [ ] **Step 1: Add the Angular API union and update fixture builders**

Add:

```typescript
export type SystemUpdateProgressStage =
  | 'downloading'
  | 'installing'
  | 'restarting'
  | 'healthChecking'
  | 'restoring'
  | 'restartingPrevious'
  | 'verifyingRecovery';
```

Add `readonly progressStage: SystemUpdateProgressStage | null;` after `phase` in `SystemUpdateStatusDto`. Add `progressStage: null` to every status-builder default in the listed specs. Keep the field required and nullable so missing API data is caught by TypeScript rather than silently treated as v1.

- [ ] **Step 2: Write failing pure progress-model tests**

Create `system-update-progress.spec.ts` with deterministic cases:

```typescript
it('shows connecting before an operation id is assigned', () => {
  const view = buildSystemUpdateProgress(
    status({ phase: 'applying', operationId: null, progressStage: null }),
    false,
    now,
  );
  expect(view.standard.map(step => [step.label, step.state])).toEqual([
    ['Connecting to update service', 'active'],
    ['Downloading verified image', 'pending'],
    ['Installing update', 'pending'],
    ['Restarting ReachCommander', 'pending'],
    ['Checking system health', 'pending'],
    ['Activating updated application', 'pending'],
  ]);
});

it('marks only confirmed detailed stages complete', () => {
  const view = buildSystemUpdateProgress(
    status({ phase: 'applying', operationId: 'operation-1', progressStage: 'installing' }),
    false,
    now,
  );
  expect(view.standard.map(step => step.state)).toEqual([
    'complete', 'complete', 'active', 'pending', 'pending', 'pending',
  ]);
});

it('uses one honest row for protocol-v1 applying status', () => {
  const view = buildSystemUpdateProgress(
    status({ phase: 'applying', operationId: 'operation-1', progressStage: null }),
    false,
    now,
  );
  expect(view.standard).toEqual([
    expect.objectContaining({ label: 'Applying trusted update', state: 'active' }),
  ]);
});
```

Also cover restart reconnection without stage regression, `healthChecking`, all three recovery stages, completed activation, rolled-back completion, failed recovery, and `stale = true` only when `now - updatedAt >= 30_000` during applying.

- [ ] **Step 3: Write failing overlay/store tests**

In the store spec, capture successive detailed applying statuses and assert the latest server stage is retained through a connection failure. Assert the optimistic pre-response status has a null operation ID and therefore maps to Connecting.

In the overlay spec, require:

```typescript
expect(fixture.nativeElement.querySelector('ol[aria-label="Update progress"]')).not.toBeNull();
expect(fixture.nativeElement.querySelector('[data-step-state="active"]')?.textContent)
  .toContain('Installing update');
expect(fixture.nativeElement.querySelector('[aria-live="polite"]')?.textContent)
  .toContain('Installing update');
```

Add reduced-motion semantic coverage by asserting that text and state attributes remain present regardless of CSS animation.

- [ ] **Step 4: Run focused Angular tests and verify RED**

Run from `client/reach-commander-ui`:

```powershell
npm test -- --watch=false --include='src/app/features/system-update/system-update-progress.spec.ts' --include='src/app/features/system-update/system-update-overlay.component.spec.ts' --include='src/app/core/state/system-update.store.spec.ts'
```

Expected: compilation/test failures because the new DTO field, pure mapper, and checklist markup do not exist.

- [ ] **Step 5: Implement the pure checklist model**

Create these focused types in `system-update-progress.ts`:

```typescript
export type UpdateProgressStepState = 'complete' | 'active' | 'pending' | 'failed';

export interface UpdateProgressStep {
  readonly id: string;
  readonly label: string;
  readonly state: UpdateProgressStepState;
}

export interface SystemUpdateProgressView {
  readonly standard: readonly UpdateProgressStep[];
  readonly recovery: readonly UpdateProgressStep[];
  readonly currentLabel: string | null;
  readonly stale: boolean;
  readonly detailed: boolean;
}

export function buildSystemUpdateProgress(
  status: SystemUpdateStatusDto,
  reconnecting: boolean,
  nowMilliseconds = Date.now(),
): SystemUpdateProgressView
```

Use fixed immutable label arrays. Derive Connecting only from `phase === 'applying' && operationId === null`. Treat applying with an operation ID and null host stage as the one-row v1 fallback. For detailed healthy stages, mark only earlier stages complete. For recovery stages, retain the standard list at its last known point and create a separate recovery list. For `completed`, mark the five pre-activation items complete and activation active. For `rolledBack`, mark recovery complete. Mark a failed active recovery item `failed` when its stage is known.

Set `stale` only for an applying operation with a non-null operation ID whose parsed `updatedAt` is valid and at least 30 seconds old. Staleness changes copy only; it must not change stage state.

- [ ] **Step 6: Render the semantic list and fixed copy**

Expose a computed `progress` from the overlay component and make recovery stages use title `Recovering previous version`. In the template, render:

```html
<div class="progress-copy" aria-live="polite" aria-atomic="true">
  {{ progress().stale ? 'Update still in progress.' : progress().currentLabel }}
</div>

<ol class="update-progress" aria-label="Update progress">
  @for (step of progress().standard; track step.id) {
    <li [attr.data-step-state]="step.state">
      <span class="step-indicator" aria-hidden="true"></span>
      <span>{{ step.label }}</span>
    </li>
  }
</ol>

@if (progress().recovery.length > 0) {
  <ol class="update-progress recovery" aria-label="Recovery progress">
    @for (step of progress().recovery; track step.id) {
      <li [attr.data-step-state]="step.state">
        <span class="step-indicator" aria-hidden="true"></span>
        <span>{{ step.label }}</span>
      </li>
    }
  </ol>
}
```

Use CSS Grid for aligned indicators and labels. Complete uses a checkmark treatment, active uses the accent plus a small pulse, pending is muted, and failed uses danger colors. Keep normal document flow, add `overflow-y: auto` to the full-screen overlay, and preserve the current centered maximum width. Under `prefers-reduced-motion: reduce`, remove the step pulse as well as ring motion. Norton-theme overrides must remain square and high contrast.

- [ ] **Step 7: Run focused and complete Angular verification**

Run from `client/reach-commander-ui`:

```powershell
npm test -- --watch=false --include='src/app/features/system-update/system-update-progress.spec.ts' --include='src/app/features/system-update/system-update-overlay.component.spec.ts' --include='src/app/core/state/system-update.store.spec.ts'
npm test -- --watch=false
npm run build
npm run test:pwa
npm run verify:pwa
```

Expected: all Angular tests PASS, the production build succeeds, and both PWA contract checks pass.

- [ ] **Step 8: Commit the updater checklist UI**

```powershell
git add client/reach-commander-ui/src/app/core/api/api.models.ts client/reach-commander-ui/src/app/core/state/system-update.store.ts client/reach-commander-ui/src/app/core/state/system-update.store.spec.ts client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts client/reach-commander-ui/src/app/features/system-update/system-update-progress.ts client/reach-commander-ui/src/app/features/system-update/system-update-progress.spec.ts client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.ts client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.html client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.scss client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.spec.ts client/reach-commander-ui/src/app/features/system-update/system-update-button.component.spec.ts client/reach-commander-ui/src/app/features/system-update/system-update-dialog.component.spec.ts client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts
git commit -m "feat: show detailed update progress"
```

---

### Task 6: Add browser acceptance, migration guidance, and the complete release gate

**Files:**
- Modify: `tests/e2e/support/seed-fixtures.ts`
- Modify: `tests/e2e/specs/system-update.spec.ts`
- Modify: `README.md`
- Modify: `deploy/README.md`
- Modify: `docs/deployment/ubuntu.md`

**Interfaces:**
- Consumes: all host, backend, and Angular behavior from Tasks 1–5.
- Produces: end-to-end stage/recovery/fallback acceptance and administrator-facing helper-refresh guidance.

- [ ] **Step 1: Extend the browser fixture contract**

Add the same `SystemUpdateProgressStage` union and required nullable `progressStage` field to `SystemUpdateFixture`; default it to null in `systemUpdateFixture(...)`.

Extend `UpdateRoutes` so a test can publish successive statuses while the existing route remains active. Continue recording Apply bodies and continue blocking service workers in this API-boundary suite.

- [ ] **Step 2: Add detailed healthy, recovery, and v1 fallback acceptance**

Add one healthy scenario that applies with `downloading`, publishes `installing`, `restarting`, and `healthChecking`, and verifies that the visible active item advances while earlier items show `data-step-state="complete"`. Publish `completed` and assert `Activating updated application` appears before the existing refresh marker is stored.

Add one recovery scenario that publishes `restoring`, `restartingPrevious`, and `verifyingRecovery`, then `rolledBack`; assert the recovery list becomes visible and ends in `Previous version restored`.

Add one legacy scenario:

```typescript
routes.applyWith(
  systemUpdateFixture({
    phase: 'applying',
    operationId: 'operation-v1',
    progressStage: null,
  }),
);

await expect(page.getByText('Applying trusted update')).toBeVisible();
await expect(page.getByText('Downloading verified image')).toHaveCount(0);
```

Extend compact acceptance to open the applying overlay at `360x560`, verify no horizontal overflow, verify the overlay can scroll vertically, and run it once in each theme. Keep the existing computed-style reduced-motion test and also assert the active step animation is `none` in reduced-motion mode.

- [ ] **Step 3: Run the focused browser scenario and verify behavior**

Run the Angular build first, then from `tests/e2e`:

```powershell
npm test -- system-update.spec.ts
```

Expected: every system-update browser case PASS, including stage advancement, rollback recovery, v1 fallback, compact themes, and reduced motion.

- [ ] **Step 4: Document protocol-v1 fallback and helper refresh**

Update all three documentation files with the same operational facts:

- detailed stage reporting requires the protocol-v2 Ubuntu helper;
- an older helper remains safe and functional but shows `Applying trusted update`;
- refreshing the checksum-verified Ubuntu installer bundle upgrades the root-owned helper without moving authentication data, keys, source configuration, or mounted source contents into the image;
- generic progress does not mean the update is stalled;
- raw host logs are intentionally not displayed in the browser;
- `sudo reachcommander doctor` remains the terminal-failure diagnostic command.

Do not claim the application container can upgrade the root-owned helper automatically.

- [ ] **Step 5: Run the complete repository verification gate**

Run from the repository root unless a working directory is stated:

```powershell
python -m unittest tests/installer/test_lan_address.py tests/installer/test_render_config.py tests/installer/test_updater_protocol.py tests/installer/test_updater_service.py -v
bash tests/installer/test_common.sh
bash tests/installer/test_install.sh
bash tests/installer/test_command.sh
bash tests/installer/test_package.sh
node --test tests/installer/release-tags.test.mjs tests/installer/workflow-contract.test.mjs tests/installer/docs-contract.test.mjs
dotnet test ReachCommander.slnx -c Release
```

Run from `client/reach-commander-ui`:

```powershell
npm test -- --watch=false
npm run test:pwa
npm run build
npm run verify:pwa
```

Run from `tests/e2e`:

```powershell
npm test
```

On Ubuntu or CI, also run:

```bash
systemd-analyze verify deploy/systemd/reachcommander-updater.service
shellcheck -x --source-path=SCRIPTDIR \
  deploy/install.sh deploy/reachcommander deploy/lib/common.sh \
  deploy/package-installer.sh tests/installer/test_common.sh \
  tests/installer/test_install.sh tests/installer/test_command.sh \
  tests/installer/test_package.sh
```

Expected: every available command exits 0 with no test failures. Record platform-only checks as CI-required when they cannot run on Windows; do not describe them as locally passing.

- [ ] **Step 6: Inspect scope and commit acceptance/documentation**

Run:

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors and only planned Task 6 files are modified. `NC-theme.png` remains untracked and unstaged.

Commit:

```powershell
git add tests/e2e/support/seed-fixtures.ts tests/e2e/specs/system-update.spec.ts README.md deploy/README.md docs/deployment/ubuntu.md
git commit -m "test: verify detailed update progress"
```

- [ ] **Step 7: Confirm final local state without pushing**

```powershell
git status --short --branch
git log -8 --oneline
```

Expected: `master` is ahead of `origin/master` by the plan and implementation commits, with only `?? NC-theme.png`. Do not push or tag until explicitly requested.

## Specification coverage

| Approved requirement | Implemented by |
|---|---|
| Real host-reported download/install/restart/health stages | Tasks 1–2 |
| Recovery after restart or health-check failure | Tasks 1–2, 4, 6 |
| Strict protocol-v2 negotiation with v1 fallback | Tasks 1 and 3 |
| Old backend compatibility with a new helper | Tasks 1 and 3 |
| Restart-resilient monotonic backend state | Tasks 3–4 |
| Client-observed connecting and activation states | Task 5 |
| Honest single-row v1 UI fallback | Tasks 5–6 |
| Stale progress explanation without stage guessing | Task 5 |
| No raw host output or sensitive details | Tasks 1–3 and 6 |
| Standard/Norton, compact, accessible, reduced-motion UI | Tasks 5–6 |
| No percentages, new transport, database, or Docker socket | Global constraints and all tasks |
| Helper-refresh migration documentation | Task 6 |
