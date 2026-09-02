using System.ComponentModel.DataAnnotations;
using ReachCommander.Application.TextEncodings;

namespace ReachCommander.Api.Contracts.TextEncodings;

public sealed record TextEncodingPreviewRequestDto(
    [Required] string SourceId,
    [Required] IReadOnlyList<string> FilePaths,
    TextEncodingKind SourceEncoding,
    TextEncodingKind OutputEncoding)
{
    public TextEncodingPreviewRequest ToModel() => new(
        SourceId,
        FilePaths,
        SourceEncoding,
        OutputEncoding);
}

public sealed record TextEncodingPreviewRowDto(
    string FilePath,
    string FileName,
    TextEncodingKind? DetectedSourceEncoding,
    TextEncodingConfidence? Confidence,
    TextEncodingPreviewStatus Status,
    string? Code,
    string? Detail,
    string PreviewText)
{
    public static TextEncodingPreviewRowDto FromModel(TextEncodingPreviewRow row) => new(
        row.FilePath,
        row.FileName,
        row.DetectedSourceEncoding,
        row.Confidence,
        row.Status,
        row.Code,
        row.Detail,
        row.PreviewText);
}

public sealed record TextEncodingPreviewDto(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<TextEncodingPreviewRowDto> Rows,
    int ReadyCount,
    int WarningCount,
    int InvalidCount,
    bool CanExecute)
{
    public static TextEncodingPreviewDto FromModel(TextEncodingPreview preview) => new(
        preview.PlanId,
        preview.ExpiresAt,
        preview.Rows.Select(TextEncodingPreviewRowDto.FromModel).ToArray(),
        preview.ReadyCount,
        preview.WarningCount,
        preview.InvalidCount,
        preview.CanExecute);
}

public sealed record TextEncodingOperationRowDto(
    string FilePath,
    string? BackupPath,
    TextEncodingRowResult Result,
    string? Code,
    string? Detail)
{
    public static TextEncodingOperationRowDto FromModel(TextEncodingOperationRow row) => new(
        row.FilePath,
        row.BackupPath,
        row.Result,
        row.Code,
        row.Detail);
}

public sealed record TextEncodingOperationDto(
    Guid OperationId,
    TextEncodingOperationState State,
    int CompletedFiles,
    int TotalFiles,
    double Percent,
    string? CurrentFileName,
    bool CanCancel,
    IReadOnlyList<TextEncodingOperationRowDto> Rows,
    string? ErrorCode,
    string? ErrorDetail)
{
    public static TextEncodingOperationDto FromModel(TextEncodingOperation operation) => new(
        operation.OperationId,
        operation.State,
        operation.CompletedFiles,
        operation.TotalFiles,
        operation.Percent,
        operation.CurrentFileName,
        operation.CanCancel,
        operation.Rows.Select(TextEncodingOperationRowDto.FromModel).ToArray(),
        operation.ErrorCode,
        operation.ErrorDetail);
}
