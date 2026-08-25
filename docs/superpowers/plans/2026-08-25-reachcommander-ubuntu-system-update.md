# ReachCommander Ubuntu System Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add automatic update discovery and administrator-confirmed, health-checked full-stack updates to installer-managed Ubuntu deployments from a toolbar control beside system telemetry.

**Architecture:** A root-owned Python `systemd` service exposes a versioned, fixed-action protocol over a protected Unix socket and delegates image replacement to the existing digest-pinned `reachcommander update` transaction. ASP.NET Core schedules checks, drains mutations, and exposes authenticated sanitized status/apply endpoints; Angular renders the update state, confirmation, restart recovery, and PWA activation without receiving Docker access or update target inputs.

**Tech Stack:** Ubuntu `systemd`, Python 3 standard library, Bash, Docker Compose, .NET 10, ASP.NET Core controllers/hosted services/Unix sockets, Angular 22 signals, Angular service worker, Vitest, Playwright, xUnit, Node test runner, and GitHub Actions.

## Global Constraints

- Execute directly on `master`; do not create a branch or worktree.
- Preserve the unrelated untracked `NC-theme.png`; never stage, modify, or remove it.
- Do not push until the user explicitly requests a push.
- Use TDD for every behavior: write a focused failing test, observe the expected failure, implement the minimum behavior, then run focused and neighboring suites.
- Support full-stack browser-triggered updates only on installer-managed Ubuntu deployments in this version.
- Check automatically, but never apply an update without authenticated administrator confirmation.
- Follow only the channel persisted by the host installer: `stable`, `edge`, or an exact semantic version pin.
- `stable` uses the latest non-draft, non-prerelease GitHub release and its matching GHCR manifest; `edge` uses the verified `edge` manifest; exact versions remain pinned.
- Determine availability by immutable target-digest inequality, not version-string ordering alone.
- Never mount `/var/run/docker.sock` into the ReachCommander application container.
- Never accept a channel, version, image, repository, URL, command, environment value, or host path from Angular or an HTTP request.
- The host protocol accepts only `check` and `applyConfiguredChannel`, protocol version `1`, a UUID request ID, no extra fields, and messages no larger than 65,536 bytes.
- Use the fixed repository `ghcr.io/dragosniamtu/reach-commander` and fixed management command `/usr/local/bin/reachcommander`.
- Retain the existing global installer lock, immutable digest state, backup, health check, rollback, doctor, and interrupted-transaction behavior.
- Check at backend startup, at authenticated shell initialization when missing/stale, and every six hours after success; retry failures with bounded backoff without concurrent checks.
- Block Apply while durable file operations or archive extraction are queued/active; drain request-scoped mutations without cancelling user work.
- Keep authentication data, Data Protection keys, source configuration, file-operation state, and source-local managed Trash outside the image transaction.
- Persist only sanitized updater state; never return physical host paths, raw Docker output, stack traces, socket payloads, or GitHub response bodies through the API.
- Preserve both themes, the compact toolbar, narrow PWA layouts, focus management, reduced motion, and existing browser-only PWA update behavior.
- An existing Ubuntu installation must run the new installer bundle once to install the host service; the container must fail closed when the helper is absent or protocol-incompatible.
- Add no runtime dependency beyond Python 3, Docker/Compose, .NET, and Angular packages already required by the project.

---

## File and contract map

- `deploy/updater_protocol.py`: exact protocol parser, channel discovery, trusted repository rules, and sanitized public snapshots.
- `deploy/updater_service.py`: Unix-socket server, atomic host journal, fixed-command Apply worker, and service lifecycle.
- `deploy/reachcommander`: version-aware update transaction and rollback state.
- `deploy/compose.updater.yaml`: Ubuntu-only Unix-socket directory mount; the shared macOS Compose template remains unchanged.
- `src/ReachCommander.Application/SystemUpdates`: public application status/service/error contracts.
- `src/ReachCommander.Infrastructure/SystemUpdates`: Unix-socket gateway, scheduler/coordinator, and mutation drain gate.
- `src/ReachCommander.Api/Contracts/SystemUpdates` and `Controllers/SystemUpdatesController.cs`: authenticated logical API mapping.
- `client/reach-commander-ui/src/app/core/state/system-update.store.ts`: one update state machine and reconnect polling.
- `client/reach-commander-ui/src/app/features/system-update`: toolbar control, confirmation dialog, and restart overlay.

### Task 1: Define the bounded host protocol and trusted update discovery

**Files:**
- Create: `deploy/updater_protocol.py`
- Create: `tests/installer/test_updater_protocol.py`

**Interfaces:**
- Consumes: protected files `state/channel`, `state/current-image`, and optional `state/current-version`; fixed GitHub repository and GHCR repository.
- Produces: `UpdaterRequest.parse(bytes)`, `UpdateSnapshot.to_journal()`, `UpdateDiscovery.check()`, `PROTOCOL_VERSION = 1`, and `MAX_MESSAGE_BYTES = 65_536` for Task 3.

- [ ] **Step 1: Write failing protocol and discovery tests**

```python
class UpdaterProtocolTests(unittest.TestCase):
    def test_apply_rejects_browser_controlled_target_fields(self):
        raw = json.dumps({
            "protocolVersion": 1,
            "requestId": str(uuid.uuid4()),
            "action": "applyConfiguredChannel",
            "image": "attacker.example/root:latest",
        }).encode()
        with self.assertRaisesRegex(ProtocolError, "unexpected fields"):
            UpdaterRequest.parse(raw)

    def test_stable_uses_latest_release_and_matching_trusted_digest(self):
        discovery = UpdateDiscovery(
            state=FakeState(channel="stable", current_digest="sha256:" + "1" * 64),
            latest_release=lambda: "v1.4.0",
            resolve_image=lambda reference: ResolvedImage(
                reference, "sha256:" + "2" * 64, "v1.4.0", "a" * 40),
        )
        result = discovery.check()
        self.assertEqual("available", result.phase)
        self.assertEqual("v1.4.0", result.target_version)
        self.assertEqual("ghcr.io/dragosniamtu/reach-commander:v1.4.0", result.target_reference)

    def test_exact_version_is_pinned_without_network_access(self):
        latest = unittest.mock.Mock(side_effect=AssertionError("network called"))
        result = UpdateDiscovery(
            state=FakeState(channel="v1.3.0", current_digest="sha256:" + "1" * 64),
            latest_release=latest,
            resolve_image=latest,
        ).check()
        self.assertEqual("unavailable", result.phase)
        self.assertEqual("version_pinned", result.reason_code)
        latest.assert_not_called()
```

Also cover `edge`, equal digest/current state, draft/prerelease/invalid stable tags, fixed repository construction, missing state, symlinks, invalid digests, oversized JSON, unknown protocol/action, duplicate JSON keys, non-UUID IDs, additional fields, bounded public details, and responses containing no supplied physical root.

- [ ] **Step 2: Run the focused tests and verify the expected import failure**

Run: `python3 -m unittest tests/installer/test_updater_protocol.py -v`

Expected: FAIL because `deploy.updater_protocol` does not exist.

- [ ] **Step 3: Implement immutable request/snapshot models and injected discovery boundaries**

```python
PROTOCOL_VERSION = 1
MAX_MESSAGE_BYTES = 65_536
TRUSTED_IMAGE_REPOSITORY = "ghcr.io/dragosniamtu/reach-commander"
STABLE_TAG = re.compile(r"^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
DIGEST = re.compile(r"^sha256:[0-9a-f]{64}$")

@dataclasses.dataclass(frozen=True)
class UpdaterRequest:
    protocol_version: int
    request_id: str
    action: str

    @classmethod
    def parse(cls, raw: bytes) -> "UpdaterRequest":
        if len(raw) > MAX_MESSAGE_BYTES:
            raise ProtocolError("request_too_large", "The updater request is too large.")
        value = json.loads(raw, object_pairs_hook=_reject_duplicate_keys)
        required = {"protocolVersion", "requestId", "action"}
        if not isinstance(value, dict) or set(value) != required:
            raise ProtocolError("invalid_request", "The updater request contains unexpected fields.")
        if value["protocolVersion"] != PROTOCOL_VERSION:
            raise ProtocolError("protocol_incompatible", "The host updater protocol is incompatible.")
        request_id = str(uuid.UUID(value["requestId"]))
        if value["action"] not in {"check", "applyConfiguredChannel"}:
            raise ProtocolError("invalid_action", "The updater action is not supported.")
        return cls(PROTOCOL_VERSION, request_id, value["action"])
```

Implement `UpdateDiscovery` with constructor-injected state, GitHub, and image-resolution functions. Validate all external metadata before constructing a fixed trusted reference. Return lower-camel phases and stable reason codes; store full digests only in the host snapshot.

- [ ] **Step 4: Run focused and installer Python tests**

Run: `python3 -m unittest tests/installer/test_updater_protocol.py tests/installer/test_render_config.py -v`

Expected: PASS; the existing renderer contracts remain unchanged.

- [ ] **Step 5: Commit**

```powershell
git add deploy/updater_protocol.py tests/installer/test_updater_protocol.py
git commit -m "feat: define trusted system update discovery"
```

### Task 2: Make the management transaction version-aware

**Files:**
- Modify: `deploy/lib/common.sh`
- Modify: `deploy/reachcommander`
- Modify: `tests/installer/fake-bin/docker`
- Modify: `tests/installer/test_command.sh`

**Interfaces:**
- Consumes: existing `rc_pull_digest`, global coordination lock, `.env`, `state/channel`, and `state/current-image`.
- Produces: validated `state/current-version`, `state/previous-version`, `rc_image_display_version <image> <channel>`, and version-aware status/update/rollback for Tasks 3 and 4.

- [ ] **Step 1: Add failing version-state and rollback tests**

```bash
test_update_records_display_version() {
  seed_installed_state stable "ghcr.io/dragosniamtu/reach-commander@sha256:${OLD_DIGEST}" 'v1.3.0'
  FAKE_DOCKER_VERSION_LABEL='v1.4.0' \
    run_command update stable
  assert_file_equals "$RC_INSTALL_ROOT/state/current-version" 'v1.4.0'
  assert_file_equals "$RC_INSTALL_ROOT/state/previous-version" 'v1.3.0'
}

test_unhealthy_update_restores_versions_with_digest_state() {
  seed_installed_state stable "ghcr.io/dragosniamtu/reach-commander@sha256:${OLD_DIGEST}" 'v1.3.0'
  printf '%s\n' unhealthy healthy >"$FAKE_DOCKER_HEALTH_FILE"
  if FAKE_DOCKER_VERSION_LABEL='v1.4.0' run_command update stable; then
    fail 'unhealthy candidate unexpectedly succeeded'
  fi
  assert_file_equals "$RC_INSTALL_ROOT/state/current-version" 'v1.3.0'
}
```

Also test edge display as `edge@<12 revision characters>`, invalid/missing OCI labels, pinned prerelease preservation, status output, backup contents, interrupted phases, doctor validation, and host-path-free errors.

- [ ] **Step 2: Run the command contract and observe missing version state**

Run: `bash tests/installer/test_command.sh`

Expected: FAIL because `current-version`/`previous-version` and label inspection are not part of the transaction.

- [ ] **Step 3: Add strict image-label resolution and include versions in every atomic transition**

```bash
rc_image_display_version() {
  local image="$1"
  local channel="$2"
  local version revision
  version="$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.version" }}' "$image")" || return 1
  revision="$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$image")" || return 1
  if [[ "$channel" == 'edge' ]]; then
    [[ "$revision" =~ ^[0-9a-f]{40}$ ]] || { rc_die 'edge image revision is invalid'; return 1; }
    printf 'edge@%.12s\n' "$revision"
    return 0
  fi
  rc_validate_channel "$version" >/dev/null 2>&1 || {
    rc_die 'image version label is invalid'
    return 1
  }
  printf '%s\n' "$version"
}
```

Add both version files to update backup/restore validation, current/previous writes, doctor required-state checks, and status output. Never use label content in a command or image reference.

- [ ] **Step 4: Run command, common, and doctor contracts**

Run: `bash tests/installer/test_common.sh && bash tests/installer/test_command.sh`

Expected: PASS, including healthy update, rollback exit `2`, failed rollback exit `3`, and interrupted-transaction recovery fixtures.

- [ ] **Step 5: Commit**

```powershell
git add deploy/lib/common.sh deploy/reachcommander tests/installer/fake-bin/docker tests/installer/test_command.sh
git commit -m "feat: track system update versions"
```

### Task 3: Implement the root-owned updater service and durable journal

**Files:**
- Create: `deploy/updater_service.py`
- Create: `deploy/systemd/reachcommander-updater.service`
- Create: `tests/installer/test_updater_service.py`

**Interfaces:**
- Consumes: Task 1 `UpdaterRequest`/`UpdateDiscovery`, Task 2 fixed `/usr/local/bin/reachcommander update`, runtime UID/GID from protected `.env`, and `/opt/.reachcommander.lock` through the command.
- Produces: Unix socket `/run/reachcommander-updater/updater.sock`, `AtomicUpdateJournal.begin/finish/read_optional`, atomic `state/system-update.json`, and `UpdaterRuntime.handle(request)` for the backend gateway.

- [ ] **Step 1: Write failing runtime, journal, apply, and socket tests**

```python
class UpdaterRuntimeTests(unittest.TestCase):
    def test_apply_uses_only_fixed_command_and_configured_channel(self):
        runner = RecordingRunner(exit_code=0)
        runtime = fixture_runtime(runner=runner, channel="stable")
        response = runtime.handle(request("applyConfiguredChannel"))
        self.assertEqual("applying", response["phase"])
        runtime.wait_for_worker()
        self.assertEqual([["/usr/local/bin/reachcommander", "update"]], runner.argv)
        self.assertNotIn("stable", runner.argv[0])

    def test_apply_is_idempotent_while_operation_is_active(self):
        runner = BlockingRunner()
        runtime = fixture_runtime(runner=runner)
        first = runtime.handle(request("applyConfiguredChannel"))
        second = runtime.handle(request("applyConfiguredChannel"))
        self.assertEqual(first["operationId"], second["operationId"])
        self.assertEqual(1, runner.start_count)

    def test_journal_is_atomic_sanitized_and_recovers_after_restart(self):
        runtime = fixture_runtime(runner=RecordingRunner(exit_code=2))
        operation = runtime.handle(request("applyConfiguredChannel"))
        runtime.wait_for_worker()
        recovered = fixture_runtime(existing_root=runtime.root).handle(request("check"))
        self.assertEqual("rolledBack", recovered["phase"])
        self.assertNotIn(str(runtime.root), json.dumps(recovered))
```

Also test exit `0` completed, `2` rolled back, `3` failed, spawn failure, fixed sanitized environment, message timeout/size, one request per connection, socket directory ownership/mode, stale journal schema, SIGTERM cleanup, and no shell invocation.

- [ ] **Step 2: Run the focused test and observe the missing service**

Run: `python3 -m unittest tests/installer/test_updater_service.py -v`

Expected: FAIL because `deploy.updater_service` and its service unit do not exist.

- [ ] **Step 3: Implement the fixed-action runtime, atomic journal, and Unix server**

```python
FIXED_COMMAND = ("/usr/local/bin/reachcommander", "update")
JOURNAL_SCHEMA = 1

class UpdaterRuntime:
    def handle(self, request: UpdaterRequest) -> dict[str, object]:
        if request.action == "check":
            return self._check(request.request_id)
        with self._lock:
            current = self._journal.read_optional()
            if current and current["phase"] == "applying":
                return protocol_response(request.request_id, current)
            snapshot = self._discovery.check()
            if snapshot.phase != "available":
                return protocol_response(request.request_id, snapshot.to_journal())
            operation = self._journal.begin(snapshot, self._clock())
            self._worker = threading.Thread(
                target=self._apply_worker,
                args=(operation,),
                name="reachcommander-update",
                daemon=True,
            )
            self._worker.start()
            return protocol_response(request.request_id, operation)

    def _apply_worker(self, operation: dict[str, object]) -> None:
        completed = subprocess.run(
            FIXED_COMMAND,
            check=False,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            timeout=300,
            env={"PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"},
        )
        phase = {0: "completed", 2: "rolledBack"}.get(completed.returncode, "failed")
        self._journal.finish(operation, phase, self._clock())

RESPONSE_FIELDS = (
    "supported", "channel", "currentVersion", "targetVersion", "currentDigest",
    "targetDigest", "phase", "reasonCode", "detail", "operationId",
    "lastCheckedAt", "updatedAt",
)

def protocol_response(request_id: str, value: dict[str, object]) -> dict[str, object]:
    response = {"protocolVersion": PROTOCOL_VERSION, "requestId": request_id}
    response.update({field: value.get(field) for field in RESPONSE_FIELDS})
    return response
```

Create the socket under a root-owned runtime directory, apply the configured runtime numeric GID, use directory mode `0750` and socket mode `0660`, read exactly one bounded newline-delimited request, and write exactly one bounded response. The unit starts only the fixed Python file, restarts on unexpected failure, grants only required network/socket/filesystem access, and never exposes request data as command arguments.

- [ ] **Step 4: Run Python tests and validate the unit text contract**

Run: `python3 -m unittest tests/installer/test_updater_protocol.py tests/installer/test_updater_service.py -v`

Run: `systemd-analyze verify deploy/systemd/reachcommander-updater.service`

Expected: PASS on Ubuntu; the local Windows environment may record `systemd-analyze` as platform-unavailable while Python tests still pass.

- [ ] **Step 5: Commit**

```powershell
git add deploy/updater_service.py deploy/systemd/reachcommander-updater.service tests/installer/test_updater_service.py
git commit -m "feat: add restricted Ubuntu update service"
```

### Task 4: Install, migrate, package, and remove the host updater safely

**Files:**
- Create: `deploy/compose.updater.yaml`
- Modify: `deploy/install.sh`
- Modify: `deploy/package-installer.sh`
- Modify: `deploy/reachcommander`
- Create: `tests/installer/fake-bin/systemctl`
- Modify: `tests/installer/test_install.sh`
- Modify: `tests/installer/test_package.sh`
- Modify: `tests/installer/workflow-contract.test.mjs`
- Modify: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: Tasks 1–3 deploy files and existing staging/reconfiguration/uninstall transactions.
- Produces: installed helper files, enabled service, Ubuntu-only `compose.override.yaml`, initialized version/journal state, and migration/uninstall contracts consumed by the backend runtime.

- [ ] **Step 1: Add failing install/reconfigure/rollback/uninstall/package tests**

```bash
test_installs_restricted_updater_without_docker_socket_mount() {
  run_installer_with_standard_answers
  assert_file_mode "$RC_INSTALL_ROOT/bin/updater_service.py" 0755
  assert_file_mode "$RC_INSTALL_ROOT/lib/updater_protocol.py" 0644
  assert_contains "$RC_INSTALL_ROOT/compose.override.yaml" '/run/reachcommander-updater'
  assert_not_contains "$RC_INSTALL_ROOT/compose.override.yaml" '/var/run/docker.sock'
  assert_systemctl_called daemon-reload
  assert_systemctl_called enable --now reachcommander-updater.service
}

test_failed_service_start_rolls_back_existing_installation() {
  seed_existing_installation
  FAKE_SYSTEMCTL_START_EXIT=1 run_installer_expect_failure
  assert_existing_deployment_unchanged
  assert_systemctl_called restart reachcommander-updater.service
}
```

Also assert `VERSION` validation/migration, source/auth state preservation, exact permissions, unit backup/restore, socket readiness before Compose, no macOS template change, deterministic archive membership/order/modes, uninstall cleanup, retained source/Trash data, and workflow execution on Ubuntu.

- [ ] **Step 2: Run installer/package/workflow contracts and observe missing artifacts**

Run: `bash tests/installer/test_install.sh && bash tests/installer/test_package.sh`

Run: `node --test tests/installer/workflow-contract.test.mjs`

Expected: FAIL because the updater artifacts, fake `systemctl`, migration, and CI gates are missing.

- [ ] **Step 3: Add the Ubuntu-only Compose override and transactional installer integration**

```yaml
services:
  reachcommander:
    volumes:
      - type: bind
        source: /run/reachcommander-updater
        target: /run/reachcommander-updater
        read_only: true
```

Require and validate the bundle `VERSION`; stage `current-version`/`previous-version`; copy the Python files and unit with atomic file helpers; start the updater and wait for its socket before starting the candidate container. Back up and restore pre-existing unit/helper/override/version state during reconfiguration. Package every added file deterministically. Uninstall stops/disables the service before removing its unit/runtime directory and never traverses configured sources.

- [ ] **Step 4: Run all installer contracts, syntax checks, and ShellCheck**

Run: `python3 -m unittest tests/installer/test_updater_protocol.py tests/installer/test_updater_service.py tests/installer/test_render_config.py -v`

Run: `bash tests/installer/test_common.sh && bash tests/installer/test_install.sh && bash tests/installer/test_command.sh && bash tests/installer/test_package.sh`

Run: `shellcheck -x --source-path=SCRIPTDIR deploy/install.sh deploy/reachcommander deploy/package-installer.sh deploy/lib/common.sh tests/installer/test_common.sh tests/installer/test_install.sh tests/installer/test_command.sh tests/installer/test_package.sh`

Expected: PASS on Ubuntu/WSL; Windows-native execution may defer Bash/systemd/ShellCheck to the Ubuntu CI gate.

- [ ] **Step 5: Commit**

```powershell
git add deploy/compose.updater.yaml deploy/install.sh deploy/package-installer.sh deploy/reachcommander tests/installer/fake-bin/systemctl tests/installer/test_install.sh tests/installer/test_package.sh tests/installer/workflow-contract.test.mjs .github/workflows/ci.yml
git commit -m "feat: install Ubuntu system updater"
```

### Task 5: Define sanitized application update contracts

**Files:**
- Create: `src/ReachCommander.Application/SystemUpdates/SystemUpdateModels.cs`
- Create: `src/ReachCommander.Application/SystemUpdates/ISystemUpdateService.cs`
- Create: `src/ReachCommander.Application/SystemUpdates/SystemUpdateExceptions.cs`
- Create: `tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateContractTests.cs`

**Interfaces:**
- Consumes: no host implementation details.
- Produces: `SystemUpdatePhase`, `SystemUpdateStatus`, `SystemUpdateStatusFactory`, `ISystemUpdateService`, and stable exceptions for Tasks 6–9.

- [ ] **Step 1: Write failing serialization and invariant tests**

```csharp
[Fact]
public void Status_serializes_logical_versions_without_host_or_full_digest()
{
    var json = JsonSerializer.Serialize(Samples.Available, JsonOptions);
    Assert.Contains("\"phase\":\"available\"", json);
    Assert.Contains("\"targetVersion\":\"v1.4.0\"", json);
    Assert.DoesNotContain("/opt/reachcommander", json, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("sha256:", json, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void Apply_contract_accepts_no_target_input()
{
    var method = typeof(ISystemUpdateService).GetMethod(nameof(ISystemUpdateService.ApplyAsync));
    Assert.Equal(new[] { typeof(CancellationToken) }, method!.GetParameters().Select(x => x.ParameterType));
}
```

Also test every phase/reason mapping, `CanApply` invariants, bounded detail, supported protocol value, and exception codes `system_update_unavailable`, `system_update_protocol_incompatible`, `system_update_check_rate_limited`, `system_update_blocked_by_operations`, `system_update_in_progress`, and `system_update_failed`.

- [ ] **Step 2: Run the focused contract test and observe missing types**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter FullyQualifiedName~SystemUpdateContractTests`

Expected: FAIL at compile time because the system-update application contracts do not exist.

- [ ] **Step 3: Add exact public models and target-free service methods**

```csharp
public enum SystemUpdatePhase
{
    Unavailable, Checking, Current, Available, Blocked,
    Applying, Completed, RolledBack, Failed,
}

public sealed record SystemUpdateStatus(
    int ProtocolVersion,
    bool Supported,
    string? Channel,
    string? CurrentVersion,
    string? TargetVersion,
    SystemUpdatePhase Phase,
    bool UpdateAvailable,
    bool CanApply,
    string? ReasonCode,
    string? Detail,
    string? OperationId,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset UpdatedAt);

public interface ISystemUpdateService
{
    Task<SystemUpdateStatus> GetAsync(CancellationToken cancellationToken);
    Task<SystemUpdateStatus> CheckAsync(CancellationToken cancellationToken);
    Task<SystemUpdateStatus> ApplyAsync(CancellationToken cancellationToken);
}

public static class SystemUpdateStatusFactory
{
    public static SystemUpdateStatus Checking(DateTimeOffset now) => new(
        1, true, null, null, null, SystemUpdatePhase.Checking,
        false, false, "system_update_checking", "Checking for updates.",
        null, null, now);
}
```

Construct statuses through invariant-enforcing factories so unsupported/pinned/checking/current states cannot be applyable and Available requires current/target versions.

- [ ] **Step 4: Run application contract and serialization neighbors**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~SystemUpdateContractTests|FullyQualifiedName~FileOperationContractTests|FullyQualifiedName~SystemMetrics"`

Expected: PASS with no host representation in the application assembly.

- [ ] **Step 5: Commit**

```powershell
git add src/ReachCommander.Application/SystemUpdates tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateContractTests.cs
git commit -m "feat: define system update contracts"
```

### Task 6: Add the Unix-socket gateway and automatic check coordinator

**Files:**
- Create: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdaterGateway.cs`
- Create: `src/ReachCommander.Infrastructure/SystemUpdates/UnixSystemUpdaterTransport.cs`
- Create: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateCoordinator.cs`
- Create: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateOptions.cs`
- Modify: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Create: `tests/ReachCommander.UnitTests/SystemUpdates/UnixSystemUpdaterGatewayTests.cs`
- Create: `tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateCoordinatorTests.cs`

**Interfaces:**
- Consumes: Task 3 protocol and Task 5 `ISystemUpdateService`.
- Produces: internal `ISystemUpdaterGateway.CheckAsync/ApplyAsync`, singleton `SystemUpdateCoordinator`, startup/six-hour scheduler, target-free Apply, and unsupported fallback.

- [ ] **Step 1: Write failing framing, mapping, scheduling, and restart-recovery tests**

```csharp
[Fact]
public async Task Gateway_sends_only_version_id_and_fixed_action()
{
    var transport = new RecordingUpdaterTransport(AvailableResponse);
    var gateway = new SystemUpdaterGateway(transport, new FixedRequestId());
    await gateway.ApplyAsync(default);
    using var request = JsonDocument.Parse(transport.SingleRequest);
    Assert.Equal(
        new[] { "action", "protocolVersion", "requestId" },
        request.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
    Assert.Equal("applyConfiguredChannel", request.RootElement.GetProperty("action").GetString());
}

[Fact]
public async Task Coordinator_checks_once_at_start_and_six_hours_after_success()
{
    var gateway = new FakeUpdaterGateway(CurrentStatus);
    var delay = new ManualSystemUpdateDelay();
    var coordinator = CreateCoordinator(gateway, delay);
    await coordinator.StartAsync(default);
    await gateway.WaitForChecksAsync(1);
    delay.Advance(TimeSpan.FromHours(6));
    await gateway.WaitForChecksAsync(2);
    Assert.Equal(2, gateway.CheckCount);
}
```

Also test 65,536-byte response bound, newline framing, timeout, malformed/duplicate fields, mismatched request ID, unknown phase, socket missing/permission denied, Windows unavailable, coalesced concurrent checks, stale authenticated refresh, retry backoff, cached status, applying journal recovery, cancellation, and physical-path redaction.

- [ ] **Step 2: Run focused tests and observe missing infrastructure types**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~UnixSystemUpdaterGatewayTests|FullyQualifiedName~SystemUpdateCoordinatorTests"`

Expected: FAIL at compile time.

- [ ] **Step 3: Implement exact JSON transport and one hosted singleton coordinator**

```csharp
internal sealed record UpdaterSnapshot(
    int ProtocolVersion,
    string Phase,
    string? Channel,
    string? CurrentVersion,
    string? TargetVersion,
    string? CurrentDigest,
    string? TargetDigest,
    string? ReasonCode,
    string? Detail,
    string? OperationId,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset UpdatedAt);

internal interface ISystemUpdaterGateway
{
    Task<UpdaterSnapshot> CheckAsync(CancellationToken cancellationToken);
    Task<UpdaterSnapshot> ApplyAsync(CancellationToken cancellationToken);
}

internal interface ISystemUpdateDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemUpdateCoordinator(
    ISystemUpdaterGateway gateway,
    ISystemUpdateDelay delay,
    TimeProvider clock) : BackgroundService, ISystemUpdateService
{
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private volatile SystemUpdateStatus _status = SystemUpdateStatusFactory.Checking(clock.GetUtcNow());

    public Task<SystemUpdateStatus> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_status);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckCoreAsync(stoppingToken).ConfigureAwait(false);
            await delay.DelayAsync(TimeSpan.FromHours(6), stoppingToken).ConfigureAwait(false);
        }
    }
}
```

Use `Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)`, explicit connect/read/write timeouts, one newline-terminated request, a 65,536-byte maximum response, strict `System.Text.Json` DTOs, request-ID equality, and sanitized mapping. Register an unavailable gateway whenever Linux/socket configuration is absent; register the coordinator once as singleton, hosted service, and `ISystemUpdateService`.

- [ ] **Step 4: Run system-update and DI tests**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter "FullyQualifiedName~SystemUpdate|FullyQualifiedName~DependencyInjection"`

Expected: PASS; Windows tests receive an explicit unsupported snapshot without socket access.

- [ ] **Step 5: Commit**

```powershell
git add src/ReachCommander.Infrastructure/SystemUpdates src/ReachCommander.Infrastructure/DependencyInjection.cs tests/ReachCommander.UnitTests/SystemUpdates
git commit -m "feat: coordinate automatic update checks"
```

### Task 7: Drain mutations and expose authenticated update APIs

**Files:**
- Create: `src/ReachCommander.Application/SystemUpdates/ISystemMutationGate.cs`
- Create: `src/ReachCommander.Infrastructure/SystemUpdates/SystemMutationGate.cs`
- Modify: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateCoordinator.cs`
- Modify: `src/ReachCommander.Infrastructure/Archives/Extraction/ArchiveExtractionOperationStore.cs`
- Create: `src/ReachCommander.Api/SystemUpdates/SystemMutationGateMiddleware.cs`
- Create: `src/ReachCommander.Api/Contracts/SystemUpdates/SystemUpdateDtos.cs`
- Create: `src/ReachCommander.Api/Controllers/SystemUpdatesController.cs`
- Create: `src/ReachCommander.Api/Errors/SystemUpdateExceptionHandler.cs`
- Modify: `src/ReachCommander.Api/Program.cs`
- Modify: `tests/ReachCommander.IntegrationTests/AuthorizationBoundaryTests.cs`
- Modify: `tests/ReachCommander.IntegrationTests/ReachCommanderApiFactory.cs`
- Create: `tests/ReachCommander.UnitTests/SystemUpdates/SystemMutationGateTests.cs`
- Create: `tests/ReachCommander.IntegrationTests/SystemUpdatesApiTests.cs`

**Interfaces:**
- Consumes: Task 5 service, Task 6 coordinator, `IFileOperationService.ListAsync`, and archive operation state.
- Produces: `GET /api/system-update`, `POST /api/system-update/check`, `POST /api/system-update/apply`, authoritative background-work blocking, and request mutation drain.

- [ ] **Step 1: Add failing concurrency, API, auth, and redaction tests**

```csharp
[Fact]
public async Task Drain_rejects_new_mutations_and_waits_for_existing_lease()
{
    var gate = new SystemMutationGate();
    await using var existing = Assert.IsAssignableFrom<IAsyncDisposable>(gate.TryEnter());
    var drain = gate.BeginDrainAsync(TimeSpan.FromSeconds(1), default);
    Assert.Null(gate.TryEnter());
    await existing.DisposeAsync();
    Assert.True(await drain);
}

[Fact]
public async Task Apply_has_no_body_requires_antiforgery_and_blocks_active_jobs()
{
    using var client = factory.CreateCookieClient();
    await client.AuthenticateAdministratorAsync();
    factory.SystemUpdates.SetAvailable();
    factory.SystemUpdates.SetBackgroundOperationsActive(true);
    var response = await client.PostAsync("/api/system-update/apply", content: null);
    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    Assert.Equal("system_update_blocked_by_operations", await response.ReadProblemCodeAsync());
    Assert.Equal(0, factory.SystemUpdates.ApplyCount);
}
```

Also test anonymous rejection for all three endpoints, antiforgery for Check/Apply, no JSON-body target, check throttling, available/current/pinned mapping, active queued/running/cancelling file operations, active archive extraction, request drain timeout, `503 update_in_progress`, host-path-free responses/logs, Apply `202`, and rollback recovery after a simulated restart.

- [ ] **Step 2: Run focused unit/integration tests and observe missing routes/gate**

Run: `dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj --filter FullyQualifiedName~SystemMutationGateTests`

Run: `dotnet test tests/ReachCommander.IntegrationTests/ReachCommander.IntegrationTests.csproj --filter "FullyQualifiedName~SystemUpdatesApiTests|FullyQualifiedName~AuthorizationBoundaryTests"`

Expected: FAIL because the gate and routes do not exist.

- [ ] **Step 3: Implement a race-free drain and thin target-free controller**

```csharp
public interface ISystemMutationGate
{
    IAsyncDisposable? TryEnter();
    Task<bool> BeginDrainAsync(TimeSpan timeout, CancellationToken cancellationToken);
    void CancelDrain();
}

[ApiController]
[Route("api/system-update")]
public sealed class SystemUpdatesController(ISystemUpdateService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<SystemUpdateStatusDto>> Get(CancellationToken token) =>
        Ok(SystemUpdateStatusDto.FromModel(await service.GetAsync(token)));

    [HttpPost("check")]
    public async Task<ActionResult<SystemUpdateStatusDto>> Check(CancellationToken token) =>
        Ok(SystemUpdateStatusDto.FromModel(await service.CheckAsync(token)));

    [HttpPost("apply")]
    public async Task<ActionResult<SystemUpdateStatusDto>> Apply(CancellationToken token) =>
        Accepted(SystemUpdateStatusDto.FromModel(await service.ApplyAsync(token)));
}
```

Middleware acquires a mutation lease for unsafe `/api` requests except `/api/system-update`; when draining, return a sanitized `503` problem. The coordinator starts drain, waits for existing leases, rechecks durable file/archive work, and only then calls the host Apply action. It leaves the process in drain after acceptance; process restart resets it. If Apply is rejected before shutdown, release drain.

- [ ] **Step 4: Run all backend tests**

Run: `dotnet test ReachCommander.slnx`

Expected: PASS with the update API protected by the same administrator cookie and global antiforgery filter.

- [ ] **Step 5: Commit**

```powershell
git add src/ReachCommander.Application/SystemUpdates/ISystemMutationGate.cs src/ReachCommander.Infrastructure/SystemUpdates src/ReachCommander.Infrastructure/Archives/Extraction/ArchiveExtractionOperationStore.cs src/ReachCommander.Api/SystemUpdates src/ReachCommander.Api/Contracts/SystemUpdates src/ReachCommander.Api/Controllers/SystemUpdatesController.cs src/ReachCommander.Api/Errors/SystemUpdateExceptionHandler.cs src/ReachCommander.Api/Program.cs tests/ReachCommander.UnitTests/SystemUpdates tests/ReachCommander.IntegrationTests
git commit -m "feat: expose safe system update APIs"
```

### Task 8: Add the Angular update API client and reconnectable store

**Files:**
- Modify: `client/reach-commander-ui/src/app/core/api/api.models.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.ts`
- Modify: `client/reach-commander-ui/src/app/core/api/reach-commander-api.spec.ts`
- Create: `client/reach-commander-ui/src/app/core/state/system-update.store.ts`
- Create: `client/reach-commander-ui/src/app/core/state/system-update.store.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/auth/protected-state-reset.service.spec.ts`

**Interfaces:**
- Consumes: Task 7 DTOs and existing authenticated `HttpClient`/antiforgery interceptor.
- Produces: `SystemUpdateStatusDto`, `CommanderApiPort.get/check/applySystemUpdate`, and `SystemUpdateStore` signals/actions for Task 9.

- [ ] **Step 1: Write failing transport and state-machine tests**

```typescript
it('posts target-free check and apply requests', async () => {
  const check = api.checkSystemUpdate();
  http.expectOne('/api/system-update/check').flush(status({ phase: 'current' }));
  await expectAsync(check).toBeResolved();
  const apply = api.applySystemUpdate();
  const request = http.expectOne('/api/system-update/apply');
  expect(request.request.body).toBeNull();
  request.flush(status({ phase: 'applying', operationId: 'operation-1' }));
  await expectAsync(apply).toBeResolved();
});

it('retains applying state across disconnects and completes after server recovery', async () => {
  api.applyResult = status({ phase: 'applying', operationId: 'operation-1' });
  await store.apply();
  api.getResults = [
    () => Promise.reject(new TypeError('Failed to fetch')),
    () => Promise.resolve(status({ phase: 'completed', operationId: 'operation-1' })),
  ];
  scheduler.runNext();
  expect(store.reconnecting()).toBe(true);
  scheduler.runNext();
  expect(pwa.refreshAfterSystemUpdate).toHaveBeenCalledOnce();
});
```

Also test initial cached load + fresh Check, six-hour backend ownership without client duplication, unsupported/pinned/current/available/blocked mappings, stale capture, one Apply, exponential reconnect cap, rollback/failure terminal states, authentication reset without cancelling host work, destroy cleanup, and reload exactly once.

- [ ] **Step 2: Run focused specs and observe missing API/store behavior**

Run from `client/reach-commander-ui`: `npm test -- --watch=false --include=src/app/core/api/reach-commander-api.spec.ts --include=src/app/core/state/system-update.store.spec.ts`

Expected: FAIL because the DTOs, port methods, and store do not exist.

- [ ] **Step 3: Implement exact DTOs, target-free client methods, and immutable state**

```typescript
export type SystemUpdatePhase =
  | 'unavailable' | 'checking' | 'current' | 'available' | 'blocked'
  | 'applying' | 'completed' | 'rolledBack' | 'failed';

export interface SystemUpdateStatusDto {
  readonly protocolVersion: number;
  readonly supported: boolean;
  readonly channel: string | null;
  readonly currentVersion: string | null;
  readonly targetVersion: string | null;
  readonly phase: SystemUpdatePhase;
  readonly updateAvailable: boolean;
  readonly canApply: boolean;
  readonly reasonCode: string | null;
  readonly detail: string | null;
  readonly operationId: string | null;
  readonly lastCheckedAt: string | null;
  readonly updatedAt: string;
}

getSystemUpdate(): Promise<SystemUpdateStatusDto> {
  return firstValueFrom(this.http.get<SystemUpdateStatusDto>('/api/system-update'));
}
checkSystemUpdate(): Promise<SystemUpdateStatusDto> {
  return firstValueFrom(this.http.post<SystemUpdateStatusDto>('/api/system-update/check', null));
}
applySystemUpdate(): Promise<SystemUpdateStatusDto> {
  return firstValueFrom(this.http.post<SystemUpdateStatusDto>('/api/system-update/apply', null));
}
```

Use a single store-owned timer; keep the last applying status through network errors; poll only applying/reconnecting states; cap reconnect delay; invoke PWA refresh once after completed; retain rollback/failure until dismissed; and register protected-state reset without issuing Apply/Cancel.

- [ ] **Step 4: Run API/store/auth neighboring specs**

Run from `client/reach-commander-ui`: `npm test -- --watch=false --include=src/app/core/api --include=src/app/core/state/system-update.store.spec.ts --include=src/app/core/auth`

Expected: PASS with no GitHub, GHCR, channel, or image logic in Angular.

- [ ] **Step 5: Commit**

```powershell
git add client/reach-commander-ui/src/app/core/api client/reach-commander-ui/src/app/core/state/system-update.store.ts client/reach-commander-ui/src/app/core/state/system-update.store.spec.ts client/reach-commander-ui/src/app/core/auth/protected-state-reset.service.spec.ts
git commit -m "feat: manage system update client state"
```

### Task 9: Add the toolbar control, confirmation, restart overlay, and PWA activation

**Files:**
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-button.component.ts`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-button.component.html`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-button.component.scss`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-button.component.spec.ts`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-dialog.component.ts`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-dialog.component.html`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-dialog.component.scss`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-dialog.component.spec.ts`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.ts`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.html`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.scss`
- Create: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.spec.ts`
- Modify: `client/reach-commander-ui/src/app/core/pwa/pwa.service.ts`
- Modify: `client/reach-commander-ui/src/app/core/pwa/pwa.service.spec.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.ts`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.html`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/commander/commander-shell/commander-shell.component.spec.ts`

**Interfaces:**
- Consumes: Task 8 `SystemUpdateStore` and existing `PwaService`/top toolbar.
- Produces: accessible update control immediately left of telemetry, immutable confirmation, blocking restart/reconnect overlay, rollback result, and one matching-shell reload.

- [ ] **Step 1: Write failing placement, state, focus, confirmation, and reload tests**

```typescript
it('places an enabled available-update control immediately before telemetry', () => {
  updateStore.status.set(status({ phase: 'available', canApply: true, targetVersion: 'v1.4.0' }));
  fixture.detectChanges();
  const actions = fixture.nativeElement.querySelector('.top-actions');
  const update = actions.querySelector('app-system-update-button');
  const metrics = actions.querySelector('app-system-metrics-widget');
  expect(update.nextElementSibling).toBe(metrics);
  expect(update.querySelector('button').getAttribute('aria-label')).toBe('Update available: v1.4.0');
});

it('captures versions and submits only after explicit confirmation', () => {
  const apply = vi.spyOn(fixture.componentInstance.apply, 'emit');
  fixture.componentRef.setInput('status', status({
    phase: 'available', currentVersion: 'v1.3.0', targetVersion: 'v1.4.0', canApply: true,
  }));
  fixture.detectChanges();
  button('Update ReachCommander').click();
  expect(apply).toHaveBeenCalledOnce();
  expect(apply).toHaveBeenCalledWith();
});
```

Also test checking/current/pinned/unsupported/blocked/applying/completed/rolledBack/failed accessible labels, disabled wrapper focusability, tooltip channel/last-check time, active-operation reason, Cancel/Escape no submit, focus trap/return, overlay reconnect state, login recovery, error dismissal, compact widths, Norton theme, reduced motion, and `PwaService.refreshAfterSystemUpdate()` activation/reload exactly once.

- [ ] **Step 2: Run focused component/PWA/shell specs and observe missing UI**

Run from `client/reach-commander-ui`: `npm test -- --watch=false --include=src/app/features/system-update --include=src/app/core/pwa/pwa.service.spec.ts --include=src/app/features/commander/commander-shell/commander-shell.component.spec.ts`

Expected: FAIL because the system-update UI and PWA refresh method do not exist.

- [ ] **Step 3: Implement focused standalone components and shell integration**

```html
<span
  class="update-control"
  [attr.tabindex]="status().canApply ? null : 0"
  [attr.title]="accessibleSummary()"
>
  <button
    type="button"
    data-testid="system-update-trigger"
    [disabled]="!status().canApply"
    [attr.aria-label]="accessibleSummary()"
    (click)="open.emit()"
  >
    <svg viewBox="0 0 20 20" fill="none" stroke="currentColor" aria-hidden="true">
      <path d="M15.5 7A6 6 0 1 0 16 12M15.5 7V3m0 4h-4" vector-effect="non-scaling-stroke" />
    </svg>
    @if (status().updateAvailable) { <i aria-hidden="true"></i> }
  </button>
</span>
```

Render `app-system-update-button` immediately before `app-system-metrics-widget`; render dialog/overlay at shell root; add them to modal keyboard gating; start/reset/destroy the store with protected shell state. The dialog displays captured versions/channel/restart/rollback text. The overlay remains during expected disconnects. Extend `PwaService` to call `checkForUpdate()`, `activateUpdate()` when available, clear its notice, and reload once after a completed system update.

- [ ] **Step 4: Run the full Angular test and production build gates**

Run from `client/reach-commander-ui`: `npm test -- --watch=false`

Run from `client/reach-commander-ui`: `npm run build`

Run from `client/reach-commander-ui`: `npm run test:pwa && node tools/verify-pwa-build.mjs`

Expected: PASS; toolbar hierarchy remains non-overlapping at 680, 1024, 1200, and 1440 pixels in both themes.

- [ ] **Step 5: Commit**

```powershell
git add client/reach-commander-ui/src/app/features/system-update client/reach-commander-ui/src/app/core/pwa client/reach-commander-ui/src/app/features/commander
git commit -m "feat: add system update toolbar flow"
```

### Task 10: Add acceptance, operator documentation, and final release gates

**Files:**
- Create: `tests/e2e/specs/system-update.spec.ts`
- Modify: `tests/e2e/support/seed-fixtures.ts`
- Modify: `README.md`
- Modify: `docs/INSTALL.md`
- Modify: `docs/deployment/ubuntu.md`
- Modify: `docs/operations.md`
- Modify: `deploy/README.md`
- Modify: `.github/workflows/ci.yml`
- Modify: `tests/installer/docs-contract.test.mjs`
- Modify: `tests/installer/workflow-contract.test.mjs`

**Interfaces:**
- Consumes: Tasks 1–9.
- Produces: deterministic browser evidence, one-time migration/runbook guidance, Ubuntu/systemd gates, and hardened no-Docker-socket assertions.

- [ ] **Step 1: Write failing browser and publication-contract tests**

```typescript
test('enables only a discovered update and recovers after the server restart', async ({ page }) => {
  await updater.routeStatus(page, currentStatus);
  await page.goto('/');
  const trigger = page.getByTestId('system-update-trigger');
  await expect(trigger).toBeDisabled();
  await updater.publish(page, availableStatus('v1.4.0'));
  await expect(trigger).toBeEnabled();
  await trigger.click();
  await page.getByRole('button', { name: 'Update ReachCommander' }).click();
  const reloaded = page.waitForEvent('framenavigated');
  await updater.disconnectThenRecover(page, completedStatus('v1.4.0'));
  await reloaded;
  await expect(page.getByRole('main')).toBeVisible();
});
```

Add browser cases for pinned/unsupported reasons, active-operation block, immutable confirmation, Cancel, rollback, failed-needs-attention, both themes, compact toolbar, and no target fields. Add documentation/workflow assertions for the systemd service, installer migration, fixed repository/channel semantics, automatic-check/manual-apply distinction, no Docker socket, updater tests before publication, and Ubuntu-only support.

- [ ] **Step 2: Run new browser/docs/workflow tests and capture the first concrete failure**

Run from `tests/e2e`: `npm test -- --grep="system update"`

Run: `node --test tests/installer/docs-contract.test.mjs tests/installer/workflow-contract.test.mjs`

Expected: FAIL at missing route fixtures and missing operator/CI text only.

- [ ] **Step 3: Complete deterministic routes, docs, and CI gates**

Use Playwright request routing only to simulate the host updater boundary while retaining the real authenticated Angular shell. Document exact commands:

```bash
sudo systemctl status reachcommander-updater.service
sudo journalctl -u reachcommander-updater.service --since today
sudo reachcommander status
sudo reachcommander doctor
```

Explain that existing installations must run the new checksum-verified installer once; automatic checks occur at startup and every six hours; Apply requires administrator confirmation; exact versions remain pinned; macOS/Windows/manual containers are unsupported; updater helper upgrades still require a future installer refresh. In CI, run Python updater contracts and `systemd-analyze verify` on Ubuntu, keep backend Windows tests unsupported-safe, include browser acceptance, and assert container mounts contain `/run/reachcommander-updater` but never `/var/run/docker.sock`.

- [ ] **Step 4: Run every available local verification gate**

Run: `python3 -m unittest tests/installer/test_updater_protocol.py tests/installer/test_updater_service.py tests/installer/test_render_config.py -v`

Run where Bash/systemd tooling is available: `bash tests/installer/test_common.sh && bash tests/installer/test_install.sh && bash tests/installer/test_command.sh && bash tests/installer/test_package.sh`

Run: `node --test tests/installer/release-tags.test.mjs tests/installer/docs-contract.test.mjs tests/installer/workflow-contract.test.mjs`

Run: `dotnet test ReachCommander.slnx`

Run from `client/reach-commander-ui`: `npm test -- --watch=false && npm run build && npm run test:pwa && node tools/verify-pwa-build.mjs`

Run from `tests/e2e`: `npm test`

Run where Docker is available: `docker build --platform linux/amd64 -t reach-commander:system-update .`

Run: `git -c safe.directory='D:/Work/Personal/Reach Commander' diff --check && git -c safe.directory='D:/Work/Personal/Reach Commander' status --short`

Expected: all available gates PASS; platform-unavailable Docker/Bash/systemd gates are reported explicitly; the only unrelated worktree entry remains `?? NC-theme.png`.

- [ ] **Step 5: Commit without pushing**

```powershell
git add tests/e2e/specs/system-update.spec.ts tests/e2e/support/seed-fixtures.ts README.md docs/INSTALL.md docs/deployment/ubuntu.md docs/operations.md deploy/README.md .github/workflows/ci.yml tests/installer/docs-contract.test.mjs tests/installer/workflow-contract.test.mjs
git commit -m "test: verify Ubuntu system updates"
```

Report the commit range, verification counts, unavailable local platform gates, one-time installer migration, and confirm `NC-theme.png` remains untracked/untouched. Do not push.

## Final acceptance matrix

| Requirement | Evidence |
|---|---|
| Automatic startup/six-hour discovery | Host discovery tests and coordinator `TimeProvider` tests |
| Stable GitHub release + GHCR digest | Python injected GitHub/registry contracts and Ubuntu CI |
| Edge digest and exact pin behavior | Protocol discovery and API/UI tests |
| Button enabled only for verified newer target | Angular component/store and Playwright tests |
| Administrator-confirmed full-stack update | Auth/CSRF API tests and confirmation E2E |
| No browser-controlled target or command | Protocol rejection, reflection/API payload, and E2E request assertions |
| No Docker socket in application | Compose, installer, workflow, and container-inspect assertions |
| Restricted root host boundary | Python protocol/service and systemd hardening contracts |
| Active-operation block and mutation drain | Gate unit tests plus API integration tests |
| Durable restart/reconnect result | Host journal, backend recovery, store, and E2E tests |
| Health rollback and rollback reporting | CLI/service fixtures, API mapping, and UI rollback test |
| Matching backend/frontend activation | Image transaction, PWA activation test, and browser success flow |
| Unsupported platform behavior | Windows backend tests and Angular disabled-state tests |
| Existing-install migration and uninstall | Installer reconfiguration/rollback/package/uninstall contracts |
| Authentication/source/Trash preservation | Installer lifecycle and hardened container acceptance |
| No physical-path/sensitive-output disclosure | Python, backend, API, log, and container assertions |
