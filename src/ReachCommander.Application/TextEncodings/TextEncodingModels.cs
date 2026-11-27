namespace ReachCommander.Application.TextEncodings;

public enum TextEncodingKind
{
    Auto,
    Utf8,
    Utf8Bom,
    Utf16LittleEndian,
    Utf16BigEndian,
    Windows1250,
    Windows1252,
    Windows1251,
    Windows1253,
    Windows1254,
    Windows1255,
    Windows1256,
    Windows1257,
    Windows1258,
    Thai874,
    ShiftJis,
    Gbk,
    Big5,
    Korean949,
    Latin1,
    Latin2,
}

public enum TextEncodingConfidence
{
    High,
    Medium,
    Low,
}

public enum TextEncodingPreviewStatus
{
    Ready,
    Warning,
    Invalid,
}

public enum TextEncodingOperationState
{
    Queued,
    Running,
    CancelRequested,
    Completed,
    CompletedWithErrors,
    Cancelled,
    Failed,
}

public enum TextEncodingRowResult
{
    Pending,
    Converted,
    Skipped,
    Failed,
    RecoveryRequired,
}

public sealed record TextEncodingPreviewRequest(
    string SourceId,
    IReadOnlyList<string> FilePaths,
    TextEncodingKind SourceEncoding,
    TextEncodingKind OutputEncoding);

public sealed record TextEncodingPreviewRow(
    string FilePath,
    string FileName,
    TextEncodingKind? DetectedSourceEncoding,
    TextEncodingConfidence? Confidence,
    TextEncodingPreviewStatus Status,
    string? Code,
    string? Detail,
    string PreviewText);

public sealed record TextEncodingPreview(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<TextEncodingPreviewRow> Rows,
    int ReadyCount,
    int WarningCount,
    int InvalidCount,
    bool CanExecute);

public sealed record TextEncodingOperationRow(
    string FilePath,
    string? BackupPath,
    TextEncodingRowResult Result,
    string? Code,
    string? Detail);

public sealed record TextEncodingOperation(
    Guid OperationId,
    TextEncodingOperationState State,
    int CompletedFiles,
    int TotalFiles,
    double Percent,
    string? CurrentFileName,
    bool CanCancel,
    IReadOnlyList<TextEncodingOperationRow> Rows,
    string? ErrorCode,
    string? ErrorDetail);
