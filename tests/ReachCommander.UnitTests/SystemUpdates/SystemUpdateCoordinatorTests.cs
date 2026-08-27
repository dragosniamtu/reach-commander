using Microsoft.Extensions.Logging.Abstractions;
using ReachCommander.Application.SystemUpdates;
using ReachCommander.Infrastructure.SystemUpdates;

namespace ReachCommander.UnitTests.SystemUpdates;

public sealed class SystemUpdateCoordinatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-25T10:00:00Z");

    [Fact]
    public async Task Coordinator_checks_once_at_start_and_six_hours_after_success()
    {
        var gateway = new FakeUpdaterGateway(CurrentSnapshot);
        var delay = new ManualSystemUpdateDelay();
        var coordinator = CreateCoordinator(gateway, delay);

        await coordinator.StartAsync(default);
        await gateway.WaitForChecksAsync(1);

        Assert.Equal(TimeSpan.FromHours(6), await delay.WaitForDelayAsync());
        delay.Advance();
        await gateway.WaitForChecksAsync(2);

        await coordinator.StopAsync(default);
        Assert.Equal(2, gateway.CheckCount);
    }

    [Fact]
    public async Task Concurrent_checks_are_coalesced()
    {
        var gateway = new GatedUpdaterGateway(CurrentSnapshot);
        var coordinator = CreateCoordinator(gateway);

        var first = coordinator.CheckAsync(default);
        var second = coordinator.CheckAsync(default);
        await gateway.WaitUntilCalledAsync();
        gateway.Release();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, gateway.CheckCount);
        Assert.Same(results[0], results[1]);
    }

    [Theory]
    [InlineData("current", SystemUpdatePhase.Current, false)]
    [InlineData("available", SystemUpdatePhase.Available, true)]
    [InlineData("applying", SystemUpdatePhase.Applying, false)]
    [InlineData("completed", SystemUpdatePhase.Completed, false)]
    [InlineData("rolledBack", SystemUpdatePhase.RolledBack, false)]
    [InlineData("failed", SystemUpdatePhase.Failed, false)]
    public async Task Coordinator_maps_host_phases_without_exposing_host_detail(
        string hostPhase,
        SystemUpdatePhase expectedPhase,
        bool canApply)
    {
        var snapshot = CurrentSnapshot with
        {
            Phase = hostPhase,
            TargetVersion = hostPhase == "current" ? null : "v1.4.0",
            ReasonCode = ReasonFor(hostPhase),
            Detail = "/opt/reachcommander sha256:secret Docker output",
            OperationId = hostPhase is "applying" or "completed" or "rolledBack" or "failed"
                ? "operation-1"
                : null,
        };
        var coordinator = CreateCoordinator(new FakeUpdaterGateway(snapshot));

        var result = await coordinator.CheckAsync(default);

        Assert.Equal(expectedPhase, result.Phase);
        Assert.Equal(canApply, result.CanApply);
        Assert.DoesNotContain("/opt/", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256:", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Docker output", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pinned_channel_is_supported_but_never_applyable()
    {
        var snapshot = CurrentSnapshot with
        {
            Channel = "v1.3.0",
            Phase = "unavailable",
            ReasonCode = "version_pinned",
            TargetVersion = null,
        };
        var coordinator = CreateCoordinator(new FakeUpdaterGateway(snapshot));

        var result = await coordinator.CheckAsync(default);

        Assert.True(result.Supported);
        Assert.False(result.CanApply);
        Assert.Equal("version_pinned", result.ReasonCode);
    }

    [Fact]
    public async Task Apply_has_no_target_and_maps_operation_id()
    {
        var available = CurrentSnapshot with { Phase = "available", ReasonCode = "update_available" };
        var applying = available with
        {
            Phase = "applying",
            ReasonCode = "update_applying",
            OperationId = "operation-1",
        };
        var gateway = new FakeUpdaterGateway(available, applying);
        var coordinator = CreateCoordinator(gateway);
        await coordinator.CheckAsync(default);

        var result = await coordinator.ApplyAsync(default);

        Assert.Equal(1, gateway.ApplyCount);
        Assert.Equal(SystemUpdatePhase.Applying, result.Phase);
        Assert.Equal("operation-1", result.OperationId);
    }

    [Fact]
    public async Task Unavailable_gateway_returns_explicit_unsupported_state()
    {
        var coordinator = CreateCoordinator(new UnavailableSystemUpdaterGateway());

        var result = await coordinator.CheckAsync(default);

        Assert.False(result.Supported);
        Assert.Equal(SystemUpdatePhase.Unavailable, result.Phase);
        Assert.Equal("system_update_unavailable", result.ReasonCode);
    }

    [Fact]
    public async Task Manual_checks_are_rate_limited_after_a_completed_check()
    {
        var coordinator = CreateCoordinator(new FakeUpdaterGateway(CurrentSnapshot));
        await coordinator.CheckAsync(default);

        await Assert.ThrowsAsync<SystemUpdateCheckRateLimitedException>(() =>
            coordinator.CheckAsync(default));
    }

    [Fact]
    public async Task Apply_blocks_active_operations_before_host_request()
    {
        var available = CurrentSnapshot with { Phase = "available", ReasonCode = "update_available" };
        var gateway = new FakeUpdaterGateway(available);
        var operations = new FakeOperationProbe { Active = true };
        var coordinator = CreateCoordinator(gateway, operations: operations);
        await coordinator.CheckAsync(default);

        await Assert.ThrowsAsync<SystemUpdateBlockedByOperationsException>(() =>
            coordinator.ApplyAsync(default));

        Assert.Equal(0, gateway.ApplyCount);
        Assert.Equal(SystemUpdatePhase.Blocked, (await coordinator.GetAsync(default)).Phase);
    }

    [Fact]
    public async Task Accepted_apply_keeps_mutations_drained()
    {
        var available = CurrentSnapshot with { Phase = "available", ReasonCode = "update_available" };
        var applying = available with
        {
            Phase = "applying",
            ReasonCode = "update_applying",
            OperationId = "operation-1",
        };
        var gate = new SystemMutationGate();
        var coordinator = CreateCoordinator(
            new FakeUpdaterGateway(available, applying),
            mutationGate: gate);
        await coordinator.CheckAsync(default);

        await coordinator.ApplyAsync(default);

        Assert.Null(gate.TryEnter());

        await Assert.ThrowsAsync<SystemUpdateInProgressException>(() =>
            coordinator.ApplyAsync(default));
        Assert.Null(gate.TryEnter());
    }

    [Fact]
    public async Task Drain_timeout_reopens_mutations_without_calling_host()
    {
        var available = CurrentSnapshot with { Phase = "available", ReasonCode = "update_available" };
        var gateway = new FakeUpdaterGateway(available);
        var gate = new RecordingMutationGate { DrainResult = false };
        var coordinator = CreateCoordinator(gateway, mutationGate: gate);
        await coordinator.CheckAsync(default);

        await Assert.ThrowsAsync<SystemUpdateFailedException>(() =>
            coordinator.ApplyAsync(default));

        Assert.Equal(1, gate.CancelCount);
        Assert.Equal(0, gateway.ApplyCount);
    }

    [Fact]
    public async Task Operation_probe_failure_fails_closed_without_leaking_detail()
    {
        var available = CurrentSnapshot with { Phase = "available", ReasonCode = "update_available" };
        var coordinator = CreateCoordinator(
            new FakeUpdaterGateway(available),
            operations: new ThrowingOperationProbe());

        var result = await coordinator.CheckAsync(default);

        Assert.Equal(SystemUpdatePhase.Blocked, result.Phase);
        Assert.False(result.CanApply);
        Assert.DoesNotContain("physical", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

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
        await gate.WaitForCancelAsync();

        Assert.Equal(1, monitor.CallCount);
        Assert.Equal(1, gate.CancelCount);
    }

    [Fact]
    public async Task Live_progress_is_same_operation_and_monotonic()
    {
        var available = CurrentSnapshot with
        {
            ProtocolVersion = 2,
            Phase = "available",
            ReasonCode = "update_available",
        };
        var applying = available with
        {
            Phase = "applying",
            ReasonCode = "update_applying",
            OperationId = "operation-1",
            ProgressStage = "downloading",
        };
        var monitor = new ControlledOperationMonitor();
        var coordinator = CreateCoordinator(
            new FakeUpdaterGateway(available, applying),
            monitor: monitor);
        await coordinator.CheckAsync(default);
        await coordinator.ApplyAsync(default);
        await monitor.WaitUntilCalledAsync();

        Assert.Equal(
            SystemUpdateProgressStage.Downloading,
            (await coordinator.GetAsync(default)).ProgressStage);

        monitor.Publish(applying with { ProgressStage = "installing" });
        Assert.Equal(
            SystemUpdateProgressStage.Installing,
            (await coordinator.GetAsync(default)).ProgressStage);

        monitor.Publish(applying with { ProgressStage = "downloading" });
        Assert.Equal(
            SystemUpdateProgressStage.Installing,
            (await coordinator.GetAsync(default)).ProgressStage);

        monitor.Publish(applying with
        {
            OperationId = "operation-other",
            ProgressStage = "restarting",
        });
        Assert.Equal(
            SystemUpdateProgressStage.Installing,
            (await coordinator.GetAsync(default)).ProgressStage);

        monitor.Publish(applying with { ProgressStage = "restarting" });
        Assert.Equal(
            SystemUpdateProgressStage.Restarting,
            (await coordinator.GetAsync(default)).ProgressStage);

        monitor.Publish(applying with { ProgressStage = "restoring" });
        Assert.Equal(
            SystemUpdateProgressStage.Restoring,
            (await coordinator.GetAsync(default)).ProgressStage);
    }

    [Fact]
    public async Task Live_trace_advances_only_for_the_matching_operation()
    {
        var available = CurrentSnapshot with
        {
            ProtocolVersion = 3,
            Phase = "available",
            ReasonCode = "update_available",
        };
        var applying = available with
        {
            Phase = "applying",
            ReasonCode = "update_applying",
            OperationId = "operation-1",
            ProgressStage = "downloading",
            Trace = Trace(sequence: 5, elapsedSeconds: 2),
        };
        var monitor = new ControlledOperationMonitor();
        var coordinator = CreateCoordinator(
            new FakeUpdaterGateway(available, applying),
            monitor: monitor);
        await coordinator.CheckAsync(default);
        await coordinator.ApplyAsync(default);
        await monitor.WaitUntilCalledAsync();

        monitor.Publish(applying with { Trace = Trace(sequence: 6, elapsedSeconds: 3) });
        Assert.Equal(6, (await coordinator.GetAsync(default)).Trace!.Events[^1].Sequence);

        monitor.Publish(applying with
        {
            ProgressStage = "installing",
            Trace = Trace(sequence: 5, elapsedSeconds: 4),
        });
        var advanced = await coordinator.GetAsync(default);
        Assert.Equal(SystemUpdateProgressStage.Installing, advanced.ProgressStage);
        Assert.Equal(6, advanced.Trace!.Events[^1].Sequence);

        monitor.Publish(applying with { Trace = Trace(sequence: 5, elapsedSeconds: 4) });
        monitor.Publish(applying with
        {
            OperationId = "operation-other",
            Trace = Trace(sequence: 7, elapsedSeconds: 5),
        });

        var status = await coordinator.GetAsync(default);
        Assert.Equal(3, status.ProtocolVersion);
        Assert.Equal("operation-1", status.OperationId);
        Assert.Equal(6, status.Trace!.Events[^1].Sequence);
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
    public async Task Restart_recovery_publishes_rollback_progress_without_releasing_foreign_drain()
    {
        var applying = CurrentSnapshot with
        {
            ProtocolVersion = 2,
            Phase = "applying",
            ReasonCode = "update_applying",
            OperationId = "operation-1",
            ProgressStage = "healthChecking",
        };
        var rolledBack = applying with
        {
            Phase = "rolledBack",
            ReasonCode = "candidate_rolled_back",
            ProgressStage = "verifyingRecovery",
        };
        var monitor = new ControlledOperationMonitor();
        var gate = new RecordingMutationGate();
        var coordinator = CreateCoordinator(
            new FakeUpdaterGateway(applying),
            mutationGate: gate,
            monitor: monitor);

        await coordinator.StartAsync(default);
        await monitor.WaitUntilCalledAsync();
        Assert.Equal(
            SystemUpdateProgressStage.HealthChecking,
            (await coordinator.GetAsync(default)).ProgressStage);

        monitor.Publish(applying with { ProgressStage = "restoring" });
        Assert.Equal(
            SystemUpdateProgressStage.Restoring,
            (await coordinator.GetAsync(default)).ProgressStage);

        monitor.Complete(new SystemUpdateMonitorResult(rolledBack));
        var terminal = await WaitForPhaseAsync(coordinator, SystemUpdatePhase.RolledBack);

        Assert.Equal(SystemUpdateProgressStage.VerifyingRecovery, terminal.ProgressStage);
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
        await gate.WaitForCancelAsync();

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

    [Fact]
    public void Dispose_is_idempotent_for_aliased_singleton_registrations()
    {
        var coordinator = CreateCoordinator(new FakeUpdaterGateway(CurrentSnapshot));

        coordinator.Dispose();
        var exception = Record.Exception(coordinator.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public async Task Transient_discovery_failure_does_not_replace_applying_status()
    {
        var applying = CurrentSnapshot with
        {
            Phase = "applying",
            ReasonCode = "update_applying",
            OperationId = "operation-1",
        };
        var monitor = new ControlledOperationMonitor();
        var coordinator = CreateCoordinator(
            new ApplyingThenUnavailableGateway(applying),
            monitor: monitor);

        await coordinator.StartAsync(default);
        await monitor.WaitUntilCalledAsync();
        var result = await coordinator.CheckAsync(default);

        Assert.Equal(SystemUpdatePhase.Applying, result.Phase);
        Assert.Equal(SystemUpdatePhase.Applying, (await coordinator.GetAsync(default)).Phase);
        await coordinator.StopAsync(default);
    }

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

    private static string ReasonFor(string phase) => phase switch
    {
        "current" => "up_to_date",
        "available" => "update_available",
        "applying" => "update_applying",
        "completed" => "update_completed",
        "rolledBack" => "candidate_rolled_back",
        _ => "update_failed",
    };

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

    private static readonly UpdaterSnapshot CurrentSnapshot = new(
        SystemUpdateStatusFactory.ProtocolVersion,
        true,
        "stable",
        "v1.3.0",
        "v1.4.0",
        "current",
        "up_to_date",
        "ReachCommander is up to date.",
        null,
        Now,
        Now);

    private static SystemUpdateTrace Trace(int sequence, long elapsedSeconds) => new(
        Now,
        elapsedSeconds,
        Now.AddSeconds(elapsedSeconds),
        [new SystemUpdateTraceEvent(
            sequence,
            Now.AddSeconds(elapsedSeconds),
            elapsedSeconds,
            SystemUpdateTraceEventCode.HostActivity,
            SystemUpdateProgressStage.Downloading,
            SystemUpdateTraceOutcome.Activity)]);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeUpdaterGateway(
        UpdaterSnapshot checkedSnapshot,
        UpdaterSnapshot? appliedSnapshot = null) : ISystemUpdaterGateway
    {
        private readonly TaskCompletionSource _checksChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CheckCount { get; private set; }

        public int ApplyCount { get; private set; }

        public Task<UpdaterSnapshot> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCount++;
            _checksChanged.TrySetResult();
            return Task.FromResult(checkedSnapshot);
        }

        public Task<UpdaterSnapshot> ApplyAsync(CancellationToken cancellationToken)
        {
            ApplyCount++;
            return Task.FromResult(appliedSnapshot ?? checkedSnapshot);
        }

        public async Task WaitForChecksAsync(int count)
        {
            var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
            while (CheckCount < count && DateTimeOffset.UtcNow < timeout)
            {
                await Task.Delay(10);
            }

            Assert.True(CheckCount >= count, $"Expected {count} checks but observed {CheckCount}.");
        }
    }

    private sealed class GatedUpdaterGateway(UpdaterSnapshot snapshot) : ISystemUpdaterGateway
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CheckCount { get; private set; }

        public async Task<UpdaterSnapshot> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCount++;
            _called.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return snapshot;
        }

        public Task<UpdaterSnapshot> ApplyAsync(CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);

        public Task WaitUntilCalledAsync() => _called.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => _release.TrySetResult();
    }

    private sealed class ApplyingThenUnavailableGateway(UpdaterSnapshot applying)
        : ISystemUpdaterGateway
    {
        private int _checkCount;

        public Task<UpdaterSnapshot> CheckAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _checkCount) == 1)
            {
                return Task.FromResult(applying);
            }

            return Task.FromException<UpdaterSnapshot>(
                new SystemUpdaterUnavailableException("temporary"));
        }

        public Task<UpdaterSnapshot> ApplyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ManualSystemUpdateDelay : ISystemUpdateDelay
    {
        private TaskCompletionSource _advance = NewCompletion();
        private readonly TaskCompletionSource<TimeSpan> _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _requested.TrySetResult(delay);
            await _advance.Task.WaitAsync(cancellationToken);
            _advance = NewCompletion();
        }

        public Task<TimeSpan> WaitForDelayAsync() => _requested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Advance() => _advance.TrySetResult();

        private static TaskCompletionSource NewCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class NeverSystemUpdateDelay : ISystemUpdateDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class FakeOperationProbe : ISystemUpdateOperationProbe
    {
        public bool Active { get; set; }

        public Task<bool> HasActiveOperationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Active);
    }

    private sealed class ThrowingOperationProbe : ISystemUpdateOperationProbe
    {
        public Task<bool> HasActiveOperationsAsync(CancellationToken cancellationToken) =>
            throw new IOException("physical /opt/reachcommander failure");
    }

    private sealed class ControlledOperationMonitor : ISystemUpdateOperationMonitor
    {
        private readonly TaskCompletionSource _called =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<SystemUpdateMonitorResult> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public bool CancellationObserved { get; private set; }

        private Action<UpdaterSnapshot>? _progress;

        public async Task<SystemUpdateMonitorResult> WaitForTerminalAsync(
            UpdaterSnapshot applyingSnapshot,
            Action<UpdaterSnapshot> progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            _progress = progress;
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

        public void Publish(UpdaterSnapshot snapshot) =>
            (_progress ?? throw new InvalidOperationException("The monitor has not started."))(
                snapshot);
    }

    private sealed class RecordingMutationGate : ISystemMutationGate
    {
        private readonly TaskCompletionSource _cancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool DrainResult { get; init; } = true;

        public int CancelCount { get; private set; }

        public IAsyncDisposable? TryEnter() => throw new NotSupportedException();

        public Task<bool> BeginDrainAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(DrainResult);

        public void CancelDrain()
        {
            CancelCount++;
            _cancelled.TrySetResult();
        }

        public Task WaitForCancelAsync() =>
            _cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
