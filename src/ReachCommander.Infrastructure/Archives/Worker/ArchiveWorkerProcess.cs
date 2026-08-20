using System.Diagnostics;

namespace ReachCommander.Infrastructure.Archives.Worker;

internal interface IArchiveWorkerProcessFactory
{
    IArchiveWorkerProcess Start(ProcessStartInfo startInfo);
}

internal interface IArchiveWorkerProcess : IAsyncDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    long WorkingSetBytes { get; }
    ValueTask CompleteInputAsync();
    ValueTask WaitForExitAsync(CancellationToken cancellationToken);
    void KillEntireProcessTree();
}

internal interface IArchiveWorkerDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class ArchiveWorkerDelay : IArchiveWorkerDelay
{
    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, cancellationToken));
}

internal sealed class ArchiveWorkerProcessFactory : IArchiveWorkerProcessFactory
{
    public IArchiveWorkerProcess Start(ProcessStartInfo startInfo) =>
        new ArchiveWorkerProcess(
            Process.Start(startInfo) ?? throw new InvalidOperationException(
                "The archive worker process could not be started."));
}

internal sealed class ArchiveWorkerProcess : IArchiveWorkerProcess
{
    private readonly Process _process;

    public ArchiveWorkerProcess(Process process)
    {
        _process = process;
        StandardInput = process.StandardInput.BaseStream;
        StandardOutput = process.StandardOutput.BaseStream;
        StandardError = process.StandardError.BaseStream;
    }

    public Stream StandardInput { get; }
    public Stream StandardOutput { get; }
    public Stream StandardError { get; }
    public bool HasExited => _process.HasExited;
    public int ExitCode => _process.ExitCode;
    public long WorkingSetBytes
    {
        get
        {
            _process.Refresh();
            return _process.WorkingSet64;
        }
    }

    public async ValueTask CompleteInputAsync() =>
        await StandardInput.DisposeAsync().ConfigureAwait(false);

    public ValueTask WaitForExitAsync(CancellationToken cancellationToken) =>
        new(_process.WaitForExitAsync(cancellationToken));

    public void KillEntireProcessTree()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StandardInput.DisposeAsync().ConfigureAwait(false);
        await StandardOutput.DisposeAsync().ConfigureAwait(false);
        await StandardError.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
    }
}
