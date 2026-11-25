namespace ReachCommander.Infrastructure.SubtitleGeneration;

public sealed class SubtitleGenerationOptions
{
    public const string SectionName = "SubtitleGeneration";
    public bool Enabled { get; set; }
    public string SharedPath { get; set; } = "/subtitle-work";
    public int MaximumQueuedJobs { get; set; } = 8;
    public int MaximumFiles { get; set; } = 10000;
    public int MaximumDirectories { get; set; } = 10000;
    public long MinimumFreeBytes { get; set; } = 64L * 1024 * 1024;
    public TimeSpan WorkerTimeout { get; set; } = TimeSpan.FromHours(6);
    public TimeSpan WorkerUnavailableGrace { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
}
