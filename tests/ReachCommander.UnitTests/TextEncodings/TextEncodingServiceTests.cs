using Microsoft.Extensions.Logging.Abstractions;
using ReachCommander.Application.TextEncodings;
using ReachCommander.Infrastructure.TextEncodings;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.TextEncodings;

public sealed class TextEncodingServiceTests
{
    [Fact]
    public async Task Execute_returns_queued_immediately_and_is_idempotent_for_a_plan()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/episode.srt", "subtitle");
        var preview = await PreviewAsync(fixture, "/TV/episode.srt");
        var runner = new BlockingRunner();
        var service = CreateService(fixture, runner);

        var first = await service.ExecuteAsync(preview.PlanId, CancellationToken.None);
        var repeated = await service.ExecuteAsync(preview.PlanId, CancellationToken.None);

        Assert.Equal(TextEncodingOperationState.Queued, first.State);
        Assert.Equal(first.OperationId, repeated.OperationId);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.Release.TrySetResult();
        await WaitForTerminalAsync(service, first.OperationId);
    }

    [Fact]
    public async Task Execute_enforces_one_active_batch_and_releases_capacity_after_completion()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/one.srt", "one");
        fixture.WriteUtf8("TV/two.srt", "two");
        var firstPreview = await PreviewAsync(fixture, "/TV/one.srt");
        var secondPreview = await PreviewAsync(fixture, "/TV/two.srt");
        var runner = new BlockingRunner();
        var service = CreateService(fixture, runner);
        var first = await service.ExecuteAsync(firstPreview.PlanId, CancellationToken.None);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var error = await Assert.ThrowsAsync<TextEncodingException>(async () =>
            await service.ExecuteAsync(secondPreview.PlanId, CancellationToken.None));
        Assert.Equal("text_encoding_capacity_reached", error.Code);

        runner.Release.TrySetResult();
        await WaitForTerminalAsync(service, first.OperationId);
        var second = await service.ExecuteAsync(secondPreview.PlanId, CancellationToken.None);
        Assert.Equal(TextEncodingOperationState.Queued, second.State);
    }

    [Fact]
    public async Task Get_returns_snapshot_and_cancel_signals_supervised_operation()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/episode.srt", "subtitle");
        var preview = await PreviewAsync(fixture, "/TV/episode.srt");
        var runner = new CancellationRunner();
        var service = CreateService(fixture, runner);
        var queued = await service.ExecuteAsync(preview.PlanId, CancellationToken.None);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(queued.OperationId, (await service.GetAsync(
            queued.OperationId,
            CancellationToken.None)).OperationId);
        var cancelling = await service.CancelAsync(queued.OperationId, CancellationToken.None);

        Assert.Equal(TextEncodingOperationState.CancelRequested, cancelling.State);
        var terminal = await WaitForTerminalAsync(service, queued.OperationId);
        Assert.Equal(TextEncodingOperationState.Cancelled, terminal.State);
    }

    [Fact]
    public async Task Unexpected_runner_exception_is_observed_and_marks_operation_failed()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/episode.srt", "subtitle");
        var preview = await PreviewAsync(fixture, "/TV/episode.srt");
        var service = CreateService(fixture, new ThrowingRunner());

        var queued = await service.ExecuteAsync(preview.PlanId, CancellationToken.None);
        var terminal = await WaitForTerminalAsync(service, queued.OperationId);

        Assert.Equal(TextEncodingOperationState.Failed, terminal.State);
        Assert.Equal("text_encoding_operation_failed", terminal.ErrorCode);
    }

    private static TextEncodingService CreateService(
        TextEncodingTestFixture fixture,
        ITextEncodingExecutor runner) => new(
            fixture.Planner,
            fixture.PlanStore,
            new TextEncodingOperationStore(fixture.Clock),
            runner,
            NullLogger<TextEncodingService>.Instance);

    private static ValueTask<TextEncodingPreview> PreviewAsync(
        TextEncodingTestFixture fixture,
        string path) => fixture.Planner.PreviewAsync(new(
            "media",
            [path],
            TextEncodingKind.Auto,
            TextEncodingKind.Utf8),
            CancellationToken.None);

    private static async Task<TextEncodingOperation> WaitForTerminalAsync(
        TextEncodingService service,
        Guid operationId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var operation = await service.GetAsync(operationId, timeout.Token);
            if (operation.State is TextEncodingOperationState.Completed or
                TextEncodingOperationState.CompletedWithErrors or
                TextEncodingOperationState.Cancelled or
                TextEncodingOperationState.Failed)
            {
                return operation;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class BlockingRunner : ITextEncodingExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(
            StoredTextEncodingPlan plan,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class CancellationRunner : ITextEncodingExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(
            StoredTextEncodingPlan plan,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ThrowingRunner : ITextEncodingExecutor
    {
        public Task RunAsync(
            StoredTextEncodingPlan plan,
            Guid operationId,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Injected failure.");
    }
}
