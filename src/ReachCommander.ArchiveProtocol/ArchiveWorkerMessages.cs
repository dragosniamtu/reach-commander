namespace ReachCommander.ArchiveProtocol;

public sealed record ArchiveWorkerLimits(
    int MaxEntries,
    long MaxTotalExtractedBytes);

public sealed record ArchiveInspectionRequest(
    byte ProtocolVersion,
    string RequestId,
    IReadOnlyList<string> VolumePaths,
    ArchiveWorkerLimits Limits)
{
    public bool Equals(ArchiveInspectionRequest? other) =>
        other is not null &&
        ProtocolVersion == other.ProtocolVersion &&
        RequestId == other.RequestId &&
        VolumePaths.SequenceEqual(other.VolumePaths, StringComparer.Ordinal) &&
        Limits == other.Limits;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ProtocolVersion);
        hash.Add(RequestId, StringComparer.Ordinal);
        foreach (var path in VolumePaths)
        {
            hash.Add(path, StringComparer.Ordinal);
        }

        hash.Add(Limits);
        return hash.ToHashCode();
    }
}

public sealed record ArchiveExtractionRequest(
    byte ProtocolVersion,
    string RequestId,
    IReadOnlyList<string> VolumePaths,
    IReadOnlyList<int> EntryIndexes,
    ArchiveWorkerLimits Limits)
{
    public bool Equals(ArchiveExtractionRequest? other) =>
        other is not null &&
        ProtocolVersion == other.ProtocolVersion &&
        RequestId == other.RequestId &&
        VolumePaths.SequenceEqual(other.VolumePaths, StringComparer.Ordinal) &&
        EntryIndexes.SequenceEqual(other.EntryIndexes) &&
        Limits == other.Limits;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ProtocolVersion);
        hash.Add(RequestId, StringComparer.Ordinal);
        foreach (var path in VolumePaths)
        {
            hash.Add(path, StringComparer.Ordinal);
        }

        foreach (var index in EntryIndexes)
        {
            hash.Add(index);
        }

        hash.Add(Limits);
        return hash.ToHashCode();
    }
}

public sealed record ArchiveDetectedFrame(string Format, bool IsSolid);

public sealed record ArchiveEntryFrame(
    int Index,
    string Key,
    bool IsDirectory,
    bool IsEncrypted,
    bool IsLink,
    bool IsSpecial,
    long? Size,
    long? CompressedSize,
    DateTimeOffset? ModifiedAt);

public sealed record ArchiveEntryStartFrame(int Index);

public sealed record ArchiveEntryEndFrame(int Index, long ActualBytes);

public sealed record ArchiveProgressFrame(int CompletedFiles, long ActualBytes);

public sealed record ArchiveCompletedFrame(int CompletedFiles, long ActualBytes);

public sealed record ArchiveFailureFrame(string Code, string Detail);
