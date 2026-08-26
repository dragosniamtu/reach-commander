# ReachCommander Update Progress Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ensure installer-managed Ubuntu updates always reach a bounded backend terminal state and make the two full-screen update rings visibly animate.

**Architecture:** Add a focused backend operation monitor that polls the existing trusted updater gateway for one operation ID, tolerates transient socket failures, and returns either a matching terminal snapshot or a six-minute timeout. `SystemUpdateCoordinator` owns one monitor task, resumes it when startup discovery observes `applying`, updates cached public state, and releases its mutation drain exactly once; Angular remains a cached-state consumer and only receives a stronger accessible ring animation.

**Tech Stack:** .NET 10 / C#, ASP.NET Core hosted services, xUnit, Angular 22, SCSS, Vitest, Playwright, Docker-host updater Unix-socket protocol

## Global Constraints

- Work directly on `master`; do not create a Git worktree.
- Preserve the unrelated untracked `NC-theme.png` file.
- The host updater command limit remains five minutes; backend recovery uses exactly six minutes, including one minute of recovery margin.
- Poll the host updater approximately once per second only while one operation is `applying`.
- `GET /api/system-update` remains cached and must not generate updater requests per browser.
- Do not add a percentage estimate, update-cancel action, new dependency, Docker-socket mount, host command, browser-supplied updater input, or additional supported platform.
- Change only the two rings in the full-screen update overlay; do not change the toolbar icon animation.
- Respect `prefers-reduced-motion: reduce` by disabling rotation while retaining static rings and progress text.
- Never expose updater responses, host paths, Docker output, stack traces, or exception messages to Angular.
- Use test-driven development and commit each independently verified task.

## File structure

- Create `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateOperationMonitor.cs` — owns operation-ID polling, transient failure tolerance, and the six-minute deadline.
- Modify `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateCoordinator.cs` — owns one monitor task, cached terminal publication, and process-owned drain release.
- Modify `src/ReachCommander.Infrastructure/DependencyInjection.cs` — registers the monitor and its delay abstraction as singletons.
- Create `tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateOperationMonitorTests.cs` — deterministic polling, retry, identity, timeout, and cancellation tests.
- Modify `tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateCoordinatorTests.cs` — coordinator lifecycle, startup recovery, coalescing, terminal state, and exact drain-release tests.
- Modify `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.scss` — clearer counter-rotating rings and reduced-motion behavior.
- Modify `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.spec.ts` — structural/accessibility coverage for the two decorative rings.
- Modify `tests/e2e/specs/system-update.spec.ts` — computed-style acceptance for normal and reduced motion.

---

### Task 1: Build the bounded host-operation monitor

**Files:**
- Create: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateOperationMonitor.cs`
- Create: `tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateOperationMonitorTests.cs`

**Interfaces:**
- Consumes: `ISystemUpdaterGateway.CheckAsync(CancellationToken)` and `UpdaterSnapshot` from `SystemUpdaterGateway.cs`; `TimeProvider`; `ILogger<SystemUpdateOperationMonitor>`.
- Produces: `ISystemUpdateOperationMonitor.WaitForTerminalAsync(UpdaterSnapshot, CancellationToken) -> Task<SystemUpdateMonitorResult>`; `ISystemUpdateMonitorDelay.DelayAsync(TimeSpan, CancellationToken)`; `SystemUpdateMonitorResult.TerminalSnapshot`.

- [ ] **Step 1: Write failing tests for matching terminal state, transient failure, operation identity, deadline, and cancellation**

Create `SystemUpdateOperationMonitorTests.cs` with these test bodies and local fakes:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using ReachCommander.Application.SystemUpdates;
using ReachCommander.Infrastructure.SystemUpdates;

namespace ReachCommander.UnitTests.SystemUpdates;

public sealed class SystemUpdateOperationMonitorTests
{
    private static readonly DateTimeOffset StartedAt =
        DateTimeOffset.Parse("2026-08-26T10:00:00Z");

    [Fact]
    public async Task Returns_matching_terminal_snapshot()
    {
        var clock = new AdjustableTimeProvider(StartedAt);
        var gateway = new SequenceGateway(
            Applying("operation-1"),
            Terminal("completed", "operation-1"));
        var monitor = CreateMonitor(gateway, clock);

        var result = await monitor.WaitForTerminalAsync(
            Applying("operation-1"), default);

        Assert.False(result.TimedOut);
        Assert.Equal("completed", result.TerminalSnapshot!.Phase);
        Assert.Equal(2, gateway.CheckCount);
    }

    [Fact]
    public async Task Retries_transient_unavailability_without_leaking_it_as_a_result()
    {
        var clock = new AdjustableTimeProvider(StartedAt);
        var gateway = new SequenceGateway(
            new SystemUpdaterUnavailableException("/run/private/updater.sock"),
            Terminal("failed", "operation-1"));
        var monitor = CreateMonitor(gateway, clock);

        var result = await monitor.WaitForTerminalAsync(
            Applying("operation-1"), default);

        Assert.Equal("failed", result.TerminalSnapshot!.Phase);
        Assert.Equal(2, gateway.CheckCount);
    }

    [Fact]
    public async Task Ignores_terminal_snapshot_for_another_operation()
    {
        var clock = new AdjustableTimeProvider(StartedAt);
        var gateway = new SequenceGateway(
            Terminal("completed", "operation-old"),
            Terminal("rolledBack", "operation-1"));
        var monitor = CreateMonitor(gateway, clock);

        var result = await monitor.WaitForTerminalAsync(
            Applying("operation-1"), default);

        Assert.Equal("rolledBack", result.TerminalSnapshot!.Phase);
        Assert.Equal("operation-1", result.TerminalSnapshot.OperationId);
    }

    [Fact]
    public async Task Times_out_six_minutes_after_journal_start()
    {
        var clock = new AdjustableTimeProvider(StartedAt);
        var gateway = new SequenceGateway(Applying("operation-1"));
        var monitor = CreateMonitor(gateway, clock);

        var result = await monitor.WaitForTerminalAsync(
            Applying("operation-1"), default);

        Assert.True(result.TimedOut);
        Assert.Null(result.TerminalSnapshot);
        Assert.Equal(StartedAt.AddMinutes(6), clock.GetUtcNow());
    }

    [Fact]
    public async Task Propagates_application_shutdown_cancellation()
    {
        var clock = new AdjustableTimeProvider(StartedAt);
        var delay = new BlockingDelay();
        var monitor = new SystemUpdateOperationMonitor(
            new SequenceGateway(Applying("operation-1")),
            delay,
            clock,
            NullLogger<SystemUpdateOperationMonitor>.Instance);
        using var cancellation = new CancellationTokenSource();

        var task = monitor.WaitForTerminalAsync(
            Applying("operation-1"), cancellation.Token);
        await delay.WaitUntilCalledAsync();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private static SystemUpdateOperationMonitor CreateMonitor(
        ISystemUpdaterGateway gateway,
        AdjustableTimeProvider clock) => new(
            gateway,
            new AdvancingDelay(clock),
            clock,
            NullLogger<SystemUpdateOperationMonitor>.Instance);

    private static UpdaterSnapshot Applying(string operationId) => new(
        SystemUpdateStatusFactory.ProtocolVersion,
        true,
        "stable",
        "v1.0.3",
        "v1.0.4",
        "applying",
        "update_applying",
        "sanitized",
        operationId,
        StartedAt,
        StartedAt);

    private static UpdaterSnapshot Terminal(string phase, string operationId) =>
        Applying(operationId) with
        {
            Phase = phase,
            ReasonCode = phase switch
            {
                "completed" => "update_completed",
                "rolledBack" => "candidate_rolled_back",
                _ => "update_failed",
            },
            UpdatedAt = StartedAt.AddSeconds(2),
        };

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class AdvancingDelay(AdjustableTimeProvider clock)
        : ISystemUpdateMonitorDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clock.Advance(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingDelay : ISystemUpdateMonitorDelay
    {
        private readonly TaskCompletionSource _called =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _called.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task WaitUntilCalledAsync() =>
            _called.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class SequenceGateway(params object[] responses)
        : ISystemUpdaterGateway
    {
        private readonly Queue<object> _responses = new(responses);
        private object? _last;
        public int CheckCount { get; private set; }

        public Task<UpdaterSnapshot> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCount++;
            if (_responses.Count > 0)
            {
                _last = _responses.Dequeue();
            }

            return _last switch
            {
                UpdaterSnapshot snapshot => Task.FromResult(snapshot),
                Exception exception => Task.FromException<UpdaterSnapshot>(exception),
                _ => throw new InvalidOperationException("A response is required."),
            };
        }

        public Task<UpdaterSnapshot> ApplyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Run the focused test and verify the missing monitor types fail compilation**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~SystemUpdateOperationMonitorTests
```

Expected: FAIL with compiler errors for `SystemUpdateOperationMonitor`, `ISystemUpdateMonitorDelay`, and `SystemUpdateMonitorResult`.

- [ ] **Step 3: Implement the bounded monitor**

Create `SystemUpdateOperationMonitor.cs` with these exact contracts and control flow:

```csharp
using Microsoft.Extensions.Logging;
using ReachCommander.Application.SystemUpdates;

namespace ReachCommander.Infrastructure.SystemUpdates;

internal interface ISystemUpdateMonitorDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemUpdateMonitorDelay(TimeProvider timeProvider)
    : ISystemUpdateMonitorDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, timeProvider, cancellationToken);
}

internal sealed record SystemUpdateMonitorResult(UpdaterSnapshot? TerminalSnapshot)
{
    public bool TimedOut => TerminalSnapshot is null;
}

internal interface ISystemUpdateOperationMonitor
{
    Task<SystemUpdateMonitorResult> WaitForTerminalAsync(
        UpdaterSnapshot applyingSnapshot,
        CancellationToken cancellationToken);
}

internal sealed class SystemUpdateOperationMonitor(
    ISystemUpdaterGateway gateway,
    ISystemUpdateMonitorDelay delay,
    TimeProvider clock,
    ILogger<SystemUpdateOperationMonitor> logger)
    : ISystemUpdateOperationMonitor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(6);

    public async Task<SystemUpdateMonitorResult> WaitForTerminalAsync(
        UpdaterSnapshot applyingSnapshot,
        CancellationToken cancellationToken)
    {
        var operationId = applyingSnapshot.OperationId;
        if (applyingSnapshot.Phase != "applying" || string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException(
                "An applying snapshot with an operation ID is required.",
                nameof(applyingSnapshot));
        }

        var deadline = applyingSnapshot.UpdatedAt.Add(MaximumDuration);
        while (clock.GetUtcNow() < deadline)
        {
            var remaining = deadline - clock.GetUtcNow();
            await delay.DelayAsync(
                remaining < PollInterval ? remaining : PollInterval,
                cancellationToken).ConfigureAwait(false);

            UpdaterSnapshot snapshot;
            try
            {
                snapshot = await gateway.CheckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is SystemUpdaterUnavailableException or SystemUpdaterProtocolException)
            {
                logger.LogDebug(
                    "System update operation monitoring will retry after {ExceptionType}.",
                    exception.GetType().Name);
                continue;
            }

            if (snapshot.ProtocolVersion != SystemUpdateStatusFactory.ProtocolVersion ||
                !snapshot.Supported ||
                !string.Equals(snapshot.OperationId, operationId, StringComparison.Ordinal))
            {
                continue;
            }

            if (snapshot.Phase is "completed" or "rolledBack" or "failed")
            {
                return new SystemUpdateMonitorResult(snapshot);
            }
        }

        return new SystemUpdateMonitorResult(null);
    }
}
```

- [ ] **Step 4: Run the monitor tests and confirm all five pass**

Run the focused command from Step 2.

Expected: PASS; 5 tests passed, 0 failed.

- [ ] **Step 5: Commit the bounded monitor**

```powershell
git add src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateOperationMonitor.cs tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateOperationMonitorTests.cs
git commit -m "fix: bound update operation monitoring"
```

---

### Task 2: Integrate monitoring with coordinator lifecycle and drain ownership

**Files:**
- Modify: `src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateCoordinator.cs`
- Modify: `src/ReachCommander.Infrastructure/DependencyInjection.cs`
- Modify: `tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateCoordinatorTests.cs`

**Interfaces:**
- Consumes: `ISystemUpdateOperationMonitor.WaitForTerminalAsync` and `SystemUpdateMonitorResult` from Task 1.
- Produces: one coalesced monitor per operation, startup recovery for `applying`, cached terminal status, six-minute timeout failure, and exact process-owned mutation-drain release.

- [ ] **Step 1: Add failing coordinator lifecycle tests**

Extend `SystemUpdateCoordinatorTests.cs` with the following tests. Add the controlled monitor helper shown after them and pass it through `CreateCoordinator` as a new optional argument.

```csharp
[Fact]
public async Task Accepted_apply_publishes_terminal_result_and_releases_drain_once()
{
    var available = CurrentSnapshot with { Phase = "available", ReasonCode = "update_available" };
    var applying = available with
    {
        Phase = "applying",
        ReasonCode = "update_applying",
        OperationId = "operation-1",
    };
    var completed = applying with
    {
        Phase = "completed",
        ReasonCode = "update_completed",
    };
    var gate = new RecordingMutationGate();
    var monitor = new ControlledOperationMonitor();
    var coordinator = CreateCoordinator(
        new FakeUpdaterGateway(available, applying),
        mutationGate: gate,
        monitor: monitor);
    await coordinator.CheckAsync(default);

    await coordinator.ApplyAsync(default);
    await monitor.WaitUntilCalledAsync();
    monitor.Complete(new SystemUpdateMonitorResult(completed));
    await WaitForPhaseAsync(coordinator, SystemUpdatePhase.Completed);

    Assert.Equal(1, monitor.CallCount);
    Assert.Equal(1, gate.CancelCount);
}

[Fact]
public async Task Applying_discovered_at_start_resumes_one_monitor_without_owning_a_drain()
{
    var applying = CurrentSnapshot with
    {
        Phase = "applying",
        ReasonCode = "update_applying",
        OperationId = "operation-1",
    };
    var monitor = new ControlledOperationMonitor();
    var gate = new RecordingMutationGate();
    var coordinator = CreateCoordinator(
        new FakeUpdaterGateway(applying),
        mutationGate: gate,
        monitor: monitor);

    await coordinator.StartAsync(default);
    await monitor.WaitUntilCalledAsync();
    await coordinator.CheckAsync(default);

    Assert.Equal(1, monitor.CallCount);
    Assert.Equal(0, gate.CancelCount);
    await coordinator.StopAsync(default);
}

[Fact]
public async Task Monitor_timeout_publishes_failed_and_releases_drain_once()
{
    var available = CurrentSnapshot with { Phase = "available", ReasonCode = "update_available" };
    var applying = available with
    {
        Phase = "applying",
        ReasonCode = "update_applying",
        OperationId = "operation-1",
    };
    var monitor = new ControlledOperationMonitor();
    var gate = new RecordingMutationGate();
    var coordinator = CreateCoordinator(
        new FakeUpdaterGateway(available, applying),
        mutationGate: gate,
        monitor: monitor);
    await coordinator.CheckAsync(default);

    await coordinator.ApplyAsync(default);
    await monitor.WaitUntilCalledAsync();
    monitor.Complete(new SystemUpdateMonitorResult(null));
    var failed = await WaitForPhaseAsync(coordinator, SystemUpdatePhase.Failed);

    Assert.Equal("update_failed", failed.ReasonCode);
    Assert.Contains("reachcommander doctor", failed.Detail, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(1, gate.CancelCount);
}

[Fact]
public async Task Shutdown_cancels_monitor_without_publishing_a_result()
{
    var applying = CurrentSnapshot with
    {
        Phase = "applying",
        ReasonCode = "update_applying",
        OperationId = "operation-1",
    };
    var monitor = new ControlledOperationMonitor();
    var coordinator = CreateCoordinator(
        new FakeUpdaterGateway(applying),
        monitor: monitor);

    await coordinator.StartAsync(default);
    await monitor.WaitUntilCalledAsync();
    await coordinator.StopAsync(default);

    Assert.True(monitor.CancellationObserved);
    Assert.Equal(SystemUpdatePhase.Applying, (await coordinator.GetAsync(default)).Phase);
}
```

Add deterministic helpers:

```csharp
private static async Task<SystemUpdateStatus> WaitForPhaseAsync(
    SystemUpdateCoordinator coordinator,
    SystemUpdatePhase phase)
{
    var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
    while (DateTimeOffset.UtcNow < timeout)
    {
        var status = await coordinator.GetAsync(default);
        if (status.Phase == phase)
        {
            return status;
        }

        await Task.Delay(10);
    }

    throw new Xunit.Sdk.XunitException($"Coordinator did not reach {phase}.");
}

private sealed class ControlledOperationMonitor : ISystemUpdateOperationMonitor
{
    private readonly TaskCompletionSource _called =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SystemUpdateMonitorResult> _result =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int CallCount { get; private set; }
    public bool CancellationObserved { get; private set; }

    public async Task<SystemUpdateMonitorResult> WaitForTerminalAsync(
        UpdaterSnapshot applyingSnapshot,
        CancellationToken cancellationToken)
    {
        CallCount++;
        _called.TrySetResult();
        try
        {
            return await _result.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancellationObserved = true;
            throw;
        }
    }

    public Task WaitUntilCalledAsync() =>
        _called.Task.WaitAsync(TimeSpan.FromSeconds(5));

    public void Complete(SystemUpdateMonitorResult result) =>
        _result.TrySetResult(result);
}
```

Change the test factory signature to:

```csharp
private static SystemUpdateCoordinator CreateCoordinator(
    ISystemUpdaterGateway gateway,
    ISystemUpdateDelay? delay = null,
    ISystemMutationGate? mutationGate = null,
    ISystemUpdateOperationProbe? operations = null,
    ISystemUpdateOperationMonitor? monitor = null) => new(
        gateway,
        delay ?? new NeverSystemUpdateDelay(),
        mutationGate ?? new SystemMutationGate(),
        operations ?? new FakeOperationProbe(),
        monitor ?? new ControlledOperationMonitor(),
        new FixedTimeProvider(Now),
        NullLogger<SystemUpdateCoordinator>.Instance);
```

- [ ] **Step 2: Run coordinator tests and verify constructor/lifecycle failures**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter FullyQualifiedName~SystemUpdateCoordinatorTests
```

Expected: FAIL because the coordinator does not accept or start `ISystemUpdateOperationMonitor`, does not publish terminal results, and does not cancel monitoring during shutdown.

- [ ] **Step 3: Add coalesced monitor ownership to `SystemUpdateCoordinator`**

Add `ISystemUpdateOperationMonitor operationMonitor` after `operationProbe` in the constructor. Add these fields:

```csharp
private readonly object _stateLock = new();
private readonly CancellationTokenSource _monitorLifetime = new();
private Task? _activeMonitor;
private string? _activeMonitorOperationId;
private bool _activeMonitorOwnsDrain;
```

Replace `GetAsync` so eligibility work cannot overwrite a terminal status published concurrently by the monitor:

```csharp
public async Task<SystemUpdateStatus> GetAsync(CancellationToken cancellationToken)
{
    cancellationToken.ThrowIfCancellationRequested();
    var observed = ReadStatus();
    var eligible = await WithOperationEligibilityAsync(observed, cancellationToken)
        .ConfigureAwait(false);

    lock (_stateLock)
    {
        if (ReferenceEquals(_status, observed))
        {
            _status = eligible;
        }

        return _status;
    }
}
```

After mapping a successful Apply response, start monitoring before returning:

```csharp
var snapshot = await gateway.ApplyAsync(cancellationToken).ConfigureAwait(false);
var applied = Map(snapshot, clock.GetUtcNow());
lock (_stateLock)
{
    _status = applied;
}

keepDrain = applied.Phase == SystemUpdatePhase.Applying;
if (keepDrain)
{
    StartOrJoinMonitor(snapshot, ownsDrain: true);
}

return applied;
```

After `CompleteCheckAsync` maps and stores a snapshot, resume monitoring when appropriate:

```csharp
var published = PublishDiscoveredStatus(status);
if (published.Phase == SystemUpdatePhase.Applying)
{
    StartOrJoinMonitor(snapshot, ownsDrain: false);
}

completion.TrySetResult(published);
```

Add these lifecycle methods. They use a completion source so a synchronously completing fake monitor cannot race assignment of `_activeMonitor`:

```csharp
private void StartOrJoinMonitor(UpdaterSnapshot snapshot, bool ownsDrain)
{
    var operationId = Required(snapshot.OperationId);
    TaskCompletionSource? owner = null;
    lock (_stateLock)
    {
        if (_activeMonitor is { IsCompleted: false })
        {
            if (string.Equals(
                    _activeMonitorOperationId,
                    operationId,
                    StringComparison.Ordinal))
            {
                _activeMonitorOwnsDrain |= ownsDrain;
            }

            return;
        }

        owner = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _activeMonitor = owner.Task;
        _activeMonitorOperationId = operationId;
        _activeMonitorOwnsDrain = ownsDrain;
    }

    _ = CompleteMonitorAsync(snapshot, owner);
}

private async Task CompleteMonitorAsync(
    UpdaterSnapshot applyingSnapshot,
    TaskCompletionSource completion)
{
    var releaseDrain = false;
    try
    {
        var result = await operationMonitor.WaitForTerminalAsync(
                applyingSnapshot,
                _monitorLifetime.Token)
            .ConfigureAwait(false);

        lock (_stateLock)
        {
            if (!ReferenceEquals(_activeMonitor, completion.Task))
            {
                return;
            }

            if (_status.Phase == SystemUpdatePhase.Applying &&
                string.Equals(
                    _status.OperationId,
                    _activeMonitorOperationId,
                    StringComparison.Ordinal))
            {
                _status = result.TerminalSnapshot is { } terminal
                    ? Map(terminal, clock.GetUtcNow())
                    : MonitorFailedStatus(_status);
            }

            releaseDrain = _activeMonitorOwnsDrain;
            ClearMonitorLocked();
        }
    }
    catch (OperationCanceledException) when (_monitorLifetime.IsCancellationRequested)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_activeMonitor, completion.Task))
            {
                ClearMonitorLocked();
            }
        }
    }
    catch (Exception exception)
    {
        logger.LogWarning(
            "System update operation monitoring failed with {ExceptionType}.",
            exception.GetType().Name);
        lock (_stateLock)
        {
            if (ReferenceEquals(_activeMonitor, completion.Task))
            {
                if (_status.Phase == SystemUpdatePhase.Applying &&
                    string.Equals(
                        _status.OperationId,
                        _activeMonitorOperationId,
                        StringComparison.Ordinal))
                {
                    _status = MonitorFailedStatus(_status);
                }

                releaseDrain = _activeMonitorOwnsDrain;
                ClearMonitorLocked();
            }
        }
    }
    finally
    {
        if (releaseDrain)
        {
            mutationGate.CancelDrain();
        }

        completion.TrySetResult();
    }
}

private SystemUpdateStatus ReadStatus()
{
    lock (_stateLock)
    {
        return _status;
    }
}

private SystemUpdateStatus PublishDiscoveredStatus(SystemUpdateStatus candidate)
{
    lock (_stateLock)
    {
        var sameOperation = string.Equals(
            _status.OperationId,
            candidate.OperationId,
            StringComparison.Ordinal);
        var currentIsTerminal = _status.Phase is
            SystemUpdatePhase.Completed or
            SystemUpdatePhase.RolledBack or
            SystemUpdatePhase.Failed;
        if (sameOperation &&
            currentIsTerminal &&
            candidate.Phase == SystemUpdatePhase.Applying)
        {
            return _status;
        }

        _status = candidate;
        return candidate;
    }
}

private void SetStatus(SystemUpdateStatus status)
{
    lock (_stateLock)
    {
        _status = status;
    }
}

private SystemUpdateStatus MonitorFailedStatus(SystemUpdateStatus current) =>
    SystemUpdateStatusFactory.Failed(
        current.Channel,
        current.CurrentVersion,
        current.TargetVersion,
        current.OperationId,
        current.LastCheckedAt,
        clock.GetUtcNow(),
        "The update status could not be recovered. Run reachcommander doctor on the host.");

private void ClearMonitorLocked()
{
    _activeMonitor = null;
    _activeMonitorOperationId = null;
    _activeMonitorOwnsDrain = false;
}

public override async Task StopAsync(CancellationToken cancellationToken)
{
    _monitorLifetime.Cancel();
    await base.StopAsync(cancellationToken).ConfigureAwait(false);

    Task? monitor;
    lock (_stateLock)
    {
        monitor = _activeMonitor;
    }

    if (monitor is not null)
    {
        await monitor.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

public override void Dispose()
{
    _monitorLifetime.Cancel();
    _monitorLifetime.Dispose();
    _applyGate.Dispose();
    base.Dispose();
}
```

Use `SetStatus(...)` for the blocked status in `ApplyAsync` and for the incompatible, unavailable, and unexpected-discovery states in `CompleteCheckAsync`. Use `PublishDiscoveredStatus(...)` for successful gateway snapshots. After this edit, no direct `_status` read or write may occur outside `_stateLock`, `ReadStatus`, `SetStatus`, `PublishDiscoveredStatus`, or the already locked monitor completion block. Keep all `_activeMonitor*` reads/writes inside `_stateLock`; never call the gateway, operation probe, logger, or mutation gate while holding the lock.

- [ ] **Step 4: Register the monitor dependencies**

In `DependencyInjection.cs`, immediately after `ISystemUpdateDelay` registration, add:

```csharp
services.AddSingleton<ISystemUpdateMonitorDelay, SystemUpdateMonitorDelay>();
services.AddSingleton<ISystemUpdateOperationMonitor, SystemUpdateOperationMonitor>();
```

- [ ] **Step 5: Run focused and complete backend tests**

Run:

```powershell
dotnet test tests/ReachCommander.UnitTests/ReachCommander.UnitTests.csproj -c Release --filter "FullyQualifiedName~SystemUpdateOperationMonitorTests|FullyQualifiedName~SystemUpdateCoordinatorTests"
dotnet test ReachCommander.slnx -c Release
```

Expected: both commands PASS; all unit and integration tests report 0 failures.

- [ ] **Step 6: Commit coordinator recovery**

```powershell
git add src/ReachCommander.Infrastructure/SystemUpdates/SystemUpdateCoordinator.cs src/ReachCommander.Infrastructure/DependencyInjection.cs tests/ReachCommander.UnitTests/SystemUpdates/SystemUpdateCoordinatorTests.cs
git commit -m "fix: reconcile applying system updates"
```

---

### Task 3: Drive and verify the full-screen ring animation in a real browser

**Files:**
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.scss`
- Modify: `client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.spec.ts`
- Modify: `tests/e2e/specs/system-update.spec.ts`

**Interfaces:**
- Consumes: existing `.spinner > i + i` markup; theme variables `--accent`, `--warning`, and `--app-bg`; Playwright's `reducedMotion` context option.
- Produces: two decorative progress arcs with `update-spin-clockwise` and `update-spin-counterclockwise`; static arcs under reduced motion; computed-style browser acceptance for both modes.

- [ ] **Step 1: Add a semantic characterization test and failing Playwright motion tests**

Add this test to `system-update-overlay.component.spec.ts`:

```typescript
it('renders two decorative progress rings while applying', () => {
  fixture.componentRef.setInput('status', status({ phase: 'applying' }));
  fixture.componentRef.setInput('reconnecting', false);
  fixture.detectChanges();

  const spinner = fixture.nativeElement.querySelector('.spinner') as HTMLElement;
  expect(spinner.getAttribute('aria-hidden')).toBe('true');
  expect(spinner.querySelectorAll(':scope > i')).toHaveLength(2);
  expect(fixture.nativeElement.textContent).toContain('Updating ReachCommander');
});
```

Append this parameterized block to `tests/e2e/specs/system-update.spec.ts`:

```typescript
for (const reducedMotion of ["no-preference", "reduce"] as const) {
  test.describe(`system update motion: ${reducedMotion}`, () => {
    test.use({ reducedMotion });

    test(`renders ${reducedMotion === "reduce" ? "static" : "counter-rotating"} progress rings`, async ({
      page,
    }) => {
      const routes = await routeSystemUpdates(page, available());
      routes.applyWith(
        systemUpdateFixture({
          targetVersion: "v1.4.0",
          phase: "applying",
          reasonCode: "update_applying",
          operationId: "operation-motion",
        }),
      );
      await page.goto("/");
      await page.getByTestId("system-update-trigger").click();
      await page.getByRole("button", { name: "Update ReachCommander" }).click();

      const overlay = page.getByRole("alertdialog", {
        name: "Updating ReachCommander",
      });
      await expect(overlay).toBeVisible();
      const styles = await overlay.locator(".spinner > i").evaluateAll((rings) =>
        rings.map((ring) => {
          const style = getComputedStyle(ring);
          return {
            animationName: style.animationName,
            animationDuration: style.animationDuration,
          };
        }),
      );

      expect(styles).toHaveLength(2);
      if (reducedMotion === "reduce") {
        expect(styles.map((style) => style.animationName)).toEqual(["none", "none"]);
      } else {
        expect(styles[0].animationName).toContain("update-spin-clockwise");
        expect(styles[1].animationName).toContain("update-spin-counterclockwise");
        expect(styles.map((style) => style.animationDuration)).toEqual(["1.15s", "0.9s"]);
      }
      await expect(overlay).toContainText(
        "The trusted update is being applied and health checked",
      );
    });
  });
}
```

- [ ] **Step 2: Run the unit characterization and prove the browser test is red against the current CSS**

Run from `client/reach-commander-ui`:

```powershell
npm test -- --watch=false --include='src/app/features/system-update/system-update-overlay.component.spec.ts'
```

Expected: PASS because the existing semantic markup is preserved.

Run from `tests/e2e`:

```powershell
npm test -- system-update.spec.ts
```

Expected: FAIL in the normal-motion case because both rings currently report the shared `update-spin` animation instead of distinct clockwise/counterclockwise names. The reduced-motion case should already report `none` for both rings.

- [ ] **Step 3: Replace the subtle shared animation with explicit counter-rotating arcs**

Replace the existing `.spinner i`, `.spinner i:last-child`, `@keyframes update-spin`, and reduced-motion rules in `system-update-overlay.component.scss` with:

```scss
.spinner i {
  position: absolute;
  inset: 0;
  border: 3px solid transparent;
  border-radius: 50%;
  transform-origin: center;
  will-change: transform;
}

.spinner i:first-child {
  border-top-color: var(--accent);
  border-right-color: color-mix(in srgb, var(--accent) 58%, transparent);
  box-shadow: 0 0 12px color-mix(in srgb, var(--accent) 26%, transparent);
  animation: update-spin-clockwise 1.15s linear infinite;
}

.spinner i:last-child {
  inset: 10px;
  border-bottom-color: var(--warning);
  border-left-color: color-mix(in srgb, var(--warning) 58%, transparent);
  animation: update-spin-counterclockwise .9s linear infinite;
}

@keyframes update-spin-clockwise {
  to { transform: rotate(360deg); }
}

@keyframes update-spin-counterclockwise {
  to { transform: rotate(-360deg); }
}

@media (prefers-reduced-motion: reduce) {
  .spinner i { animation: none; will-change: auto; }
}
```

Do not alter `system-update-button.component.scss`.

- [ ] **Step 4: Run the focused frontend and browser tests**

Run from `client/reach-commander-ui`:

```powershell
npm test -- --watch=false --include='src/app/features/system-update/system-update-overlay.component.spec.ts'
npm run build
```

Run from `tests/e2e`:

```powershell
npm test -- system-update.spec.ts
```

Expected: component test PASS, Angular production build succeeds, and every system-update browser case—including both new motion cases—passes.

- [ ] **Step 5: Run the complete local verification gate**

Run from the repository root unless a command specifies a directory:

```powershell
dotnet test ReachCommander.slnx -c Release
```

Run from `client/reach-commander-ui`:

```powershell
npm test -- --watch=false
npm run build
npm run test:pwa
```

Run from `tests/e2e`:

```powershell
npm test
```

Expected: every command exits 0; backend, Angular, PWA, and browser suites report no failures.

- [ ] **Step 6: Inspect the final diff and verify scope**

Run:

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors; only the planned updater-monitor, coordinator, DI, test, overlay SCSS, and system-update acceptance files are changed. `NC-theme.png` remains untracked and unstaged.

- [ ] **Step 7: Commit the overlay animation and its acceptance coverage**

```powershell
git add client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.scss client/reach-commander-ui/src/app/features/system-update/system-update-overlay.component.spec.ts tests/e2e/specs/system-update.spec.ts
git commit -m "feat: animate update progress rings"
```

- [ ] **Step 8: Re-run repository status after all commits**

```powershell
git status --short --branch
```

Expected: `master` is ahead of `origin/master` by the implementation commits, with only `?? NC-theme.png`; no push or release tag is created unless the user requests it.
