using ReachCommander.Application.FileOperations;
using ReachCommander.Domain.Files;
using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.Infrastructure.FileOperations.Planning;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.FileOperations;

public sealed class FileOperationRepositoryTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.Parse("2026-08-25T10:00:00Z"));
    private readonly FileOperationDataPaths _paths;

    public FileOperationRepositoryTests() =>
        _paths = FileOperationDataPaths.FromAuthenticationRoot(_temporary.Path);

    [Fact]
    public async Task RecoverAsync_interrupts_uncertain_job_and_preserves_queued_fifo_order()
    {
        var repository = new FileOperationRepository(_paths, _clock);
        var first = await repository.EnqueueAsync(Plan("/first.txt"), Approval(), default);
        var second = await repository.EnqueueAsync(Plan("/second.txt"), Approval(), default);
        var third = await repository.EnqueueAsync(Plan("/third.txt"), Approval(), default);
        var claimed = await repository.TryTakeNextAsync(default);
        Assert.Equal(first.OperationId, claimed!.OperationId);
        await repository.UpdateAsync(first.OperationId, document => document with
        {
            Status = document.Status with { Phase = FileOperationPhase.Running },
        }, default);

        var restarted = new FileOperationRepository(_paths, _clock);
        await restarted.RecoverAsync(default);

        Assert.Equal(FileOperationPhase.Interrupted, (await restarted.GetAsync(first.OperationId, default)).Phase);
        var next = await restarted.TryTakeNextAsync(default);
        Assert.Equal(second.OperationId, next!.OperationId);
        await CompleteAsync(restarted, second.OperationId);
        var last = await restarted.TryTakeNextAsync(default);
        Assert.Equal(third.OperationId, last!.OperationId);
    }

    [Fact]
    public async Task RequestCancellationAsync_cancels_queued_job_without_claiming_it()
    {
        var repository = new FileOperationRepository(_paths, _clock);
        var queued = await repository.EnqueueAsync(Plan("/queued.txt"), Approval(), default);

        var cancelled = await repository.RequestCancellationAsync(queued.OperationId, default);

        Assert.Equal(FileOperationPhase.Cancelled, cancelled.Phase);
        Assert.Null(await repository.TryTakeNextAsync(default));
    }

    [Fact]
    public async Task UpdateAsync_rejects_non_monotonic_progress_and_phase_regression()
    {
        var repository = new FileOperationRepository(_paths, _clock);
        var queued = await repository.EnqueueAsync(Plan("/movie.mkv"), Approval(), default);
        await repository.TryTakeNextAsync(default);
        await repository.UpdateAsync(queued.OperationId, document => document with
        {
            Status = document.Status with
            {
                Phase = FileOperationPhase.Running,
                Progress = document.Status.Progress with { CompletedBytes = 10 },
            },
        }, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.UpdateAsync(queued.OperationId, document => document with
            {
                Status = document.Status with
                {
                    Phase = FileOperationPhase.Queued,
                    Progress = document.Status.Progress with { CompletedBytes = 5 },
                },
            }, default));
    }

    [Fact]
    public async Task UpdateAsync_invokes_update_delegate_once()
    {
        var repository = new FileOperationRepository(_paths, _clock);
        var queued = await repository.EnqueueAsync(Plan("/movie.mkv"), Approval(), default);
        await repository.TryTakeNextAsync(default);
        var calls = 0;

        await repository.UpdateAsync(queued.OperationId, document =>
        {
            calls++;
            return document with
            {
                Status = document.Status with { Phase = FileOperationPhase.Running },
            };
        }, default);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Terminal_history_retains_newest_one_hundred_records_after_reload()
    {
        var repository = new FileOperationRepository(_paths, _clock);
        Guid oldest = default;
        for (var index = 0; index < 101; index++)
        {
            var status = await repository.EnqueueAsync(Plan($"/{index}.txt"), Approval(), default);
            if (index == 0) oldest = status.OperationId;
            await repository.TryTakeNextAsync(default);
            await CompleteAsync(repository, status.OperationId);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var restarted = new FileOperationRepository(_paths, _clock);
        var statuses = await restarted.ListAsync(default);

        Assert.Equal(100, statuses.Count);
        Assert.DoesNotContain(statuses, status => status.OperationId == oldest);
    }

    [Fact]
    public async Task Persisted_documents_contain_logical_paths_but_not_configured_roots()
    {
        var repository = new FileOperationRepository(_paths, _clock);
        var status = await repository.EnqueueAsync(Plan("/movie.mkv"), Approval(), default);

        var json = await File.ReadAllTextAsync(_paths.OperationPath(status.OperationId));

        Assert.Contains("/movie.mkv", json);
        Assert.DoesNotContain(_temporary.Path, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcknowledgeAsync_marks_only_terminal_jobs()
    {
        var repository = new FileOperationRepository(_paths, _clock);
        var status = await repository.EnqueueAsync(Plan("/movie.mkv"), Approval(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.AcknowledgeAsync(status.OperationId, default));
        await repository.TryTakeNextAsync(default);
        await CompleteAsync(repository, status.OperationId);

        await repository.AcknowledgeAsync(status.OperationId, default);

        Assert.True((await repository.GetAsync(status.OperationId, default)).Acknowledged);
    }

    public void Dispose() => _temporary.Dispose();

    private FileOperationPlan Plan(string sourcePath)
    {
        var fingerprint = new FileOperationEntryFingerprint(
            FileEntryType.File,
            10,
            _clock.GetUtcNow(),
            FileAttributes.Normal,
            false);
        return new FileOperationPlan(
            Guid.NewGuid(),
            _clock.GetUtcNow(),
            _clock.GetUtcNow().AddMinutes(10),
            FileOperationKind.Copy,
            "media",
            [sourcePath],
            "downloads",
            "/",
            [new(sourcePath, sourcePath, sourcePath, fingerprint, null, true)],
            [],
            null,
            [],
            [new DirectoryMutationTarget("downloads", "/")],
            10);
    }

    private static FileOperationSubmissionApproval Approval() => new([], false);

    private static async Task CompleteAsync(FileOperationRepository repository, Guid operationId)
    {
        await repository.UpdateAsync(operationId, document => document with
        {
            Status = document.Status with { Phase = FileOperationPhase.Running },
        }, default);
        await repository.UpdateAsync(operationId, document => document with
        {
            Status = document.Status with { Phase = FileOperationPhase.Completed },
        }, default);
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan duration) => _current = _current.Add(duration);
    }
}
