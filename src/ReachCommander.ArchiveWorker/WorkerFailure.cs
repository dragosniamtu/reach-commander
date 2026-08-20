using ReachCommander.ArchiveProtocol;

namespace ReachCommander.ArchiveWorker;

internal sealed class WorkerFailure : Exception
{
    private WorkerFailure(string code, string detail)
        : base(detail)
    {
        Frame = new ArchiveFailureFrame(code, detail);
    }

    public ArchiveFailureFrame Frame { get; }

    public static WorkerFailure Unsupported() =>
        new("archive_unsupported", "This archive format is not supported.");

    public static WorkerFailure Invalid(bool volumeSet) =>
        volumeSet ? VolumeSet() :
            new("archive_invalid", "The archive signature or structure is invalid.");

    public static WorkerFailure Encrypted() =>
        new("archive_encrypted", "Encrypted archives are not supported.");

    public static WorkerFailure VolumeSet() =>
        new(
            "archive_volume_set_invalid",
            "The archive volume set is incomplete or inconsistent.");

    public static WorkerFailure Limit() =>
        new("archive_limit_exceeded", "The archive exceeds configured worker limits.");

    public static WorkerFailure Protocol() => Invalid(volumeSet: false);

    public static WorkerFailure Unexpected() =>
        new("archive_worker_failed", "The isolated archive worker failed.");
}
