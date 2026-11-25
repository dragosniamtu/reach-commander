namespace ReachCommander.Infrastructure.SubtitleGeneration;

internal sealed record SubtitleVideo(
    string SourceId, string LogicalPath, string PhysicalPath, string DirectoryLogicalPath,
    long Length, long LastWriteTimeUtcTicks);

internal sealed record SubtitleEngineResult(string EnglishSrt, string? RomanianSrt);
internal sealed record SubtitleEngineProgress(string Stage, int Percent);

internal interface ISubtitleEngine
{
    Task<SubtitleEngineResult> GenerateAsync(SubtitleVideo video, bool romanian,
        Func<SubtitleEngineProgress, Task> progress, CancellationToken cancellationToken);
    Task CancelAbandonedAsync(CancellationToken cancellationToken);
}

internal interface ISubtitleInstaller
{
    Task<bool> IsAlreadyGeneratedAsync(SubtitleVideo video, bool english, bool romanian,
        CancellationToken cancellationToken);
    Task InstallAsync(SubtitleVideo video, bool english, bool romanian, SubtitleEngineResult result,
        CancellationToken cancellationToken);
    Task RecoverAsync(CancellationToken cancellationToken);
}
