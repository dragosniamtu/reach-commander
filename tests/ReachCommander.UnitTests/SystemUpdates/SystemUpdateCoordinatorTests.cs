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
        ISystemUpdateDelay? delay = null) => new(
            gateway,
            delay ?? new NeverSystemUpdateDelay(),
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
}
