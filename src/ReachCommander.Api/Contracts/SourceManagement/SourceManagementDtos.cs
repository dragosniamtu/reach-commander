using System.ComponentModel.DataAnnotations;
using ReachCommander.Application.SourceManagement;

namespace ReachCommander.Api.Contracts.SourceManagement;

public sealed record SourceManagementCapabilityDto(
    bool Supported,
    string ReasonCode,
    string Detail)
{
    public static SourceManagementCapabilityDto FromModel(
        SourceManagementCapability capability) => new(
            capability.Supported,
            capability.ReasonCode,
            capability.Detail);
}

public sealed record SourceAddRequestDto(
    [Required, StringLength(80, MinimumLength = 1)] string DisplayName,
    [Required, StringLength(1024, MinimumLength = 2)] string HostPath,
    [Required] string Access) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Any(char.IsControl))
        {
            yield return new ValidationResult(
                "The display name is invalid.",
                [nameof(DisplayName)]);
        }

        if (string.IsNullOrEmpty(HostPath) ||
            !HostPath.StartsWith("/", StringComparison.Ordinal) ||
            HostPath.Contains('\\') ||
            HostPath.Any(char.IsControl))
        {
            yield return new ValidationResult(
                "The host path must be an absolute Ubuntu path.",
                [nameof(HostPath)]);
        }

        if (Access is not ("readOnly" or "readWrite"))
        {
            yield return new ValidationResult(
                "The source access policy is invalid.",
                [nameof(Access)]);
        }
    }

    public SourceAddRequest ToModel() =>
        new(
            DisplayName.Trim(),
            HostPath,
            Access == "readOnly" ? SourceAccess.ReadOnly : SourceAccess.ReadWrite);
}

public sealed record SourceManagementOperationDto(
    Guid OperationId,
    string? SourceId,
    string? DisplayName,
    SourceManagementPhase Phase,
    string ReasonCode,
    string Detail,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static SourceManagementOperationDto FromModel(
        SourceManagementOperation operation) => new(
            operation.OperationId,
            operation.SourceId,
            operation.DisplayName,
            operation.Phase,
            operation.ReasonCode,
            operation.Detail,
            operation.CreatedAt,
            operation.UpdatedAt);
}
