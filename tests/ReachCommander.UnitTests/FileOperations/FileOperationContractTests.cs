using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReachCommander.Application.Directories;
using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Trash;

namespace ReachCommander.UnitTests.FileOperations;

public sealed class FileOperationContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void Status_serializes_public_phases_as_camel_case_strings()
    {
        var status = new FileOperationStatus(
            Guid.NewGuid(),
            FileOperationKind.Copy,
            FileOperationPhase.CompletedWithErrors,
            0,
            DateTimeOffset.Parse("2026-08-25T10:00:00Z"),
            DateTimeOffset.Parse("2026-08-25T10:01:00Z"),
            new FileOperationProgress(null, 1, 1, 5, 5, 100, 5, TimeSpan.FromMinutes(1), TimeSpan.Zero),
            [],
            [],
            false);

        var json = JsonSerializer.Serialize(status, JsonOptions);

        Assert.Contains("\"phase\":\"completedWithErrors\"", json);
        Assert.Contains("\"kind\":\"copy\"", json);
    }

    [Fact]
    public void Permanent_delete_warning_is_exact() => Assert.Equal(
        "This deletion is permanent, cannot be undone, and is unrecoverable.",
        PermanentDeleteConfirmation.Warning);

    [Fact]
    public void Public_operation_contracts_do_not_expose_physical_path_properties()
    {
        Type[] contractTypes =
        [
            typeof(FileOperationPreviewRequest),
            typeof(FileOperationPreview),
            typeof(FileOperationStatus),
            typeof(DeletePreviewRequest),
            typeof(DeletePreview),
            typeof(TrashEntry),
            typeof(RestorePreview),
            typeof(CreateDirectoryRequest),
        ];

        var exposed = contractTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            .Where(property => property.Name.Contains("Physical", StringComparison.OrdinalIgnoreCase))
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .ToArray();

        Assert.Empty(exposed);
    }

    [Theory]
    [InlineData(typeof(OperationPlanNotFoundException), "operation_plan_not_found")]
    [InlineData(typeof(OperationPlanExpiredException), "operation_plan_expired")]
    [InlineData(typeof(OperationPlanStaleException), "operation_plan_stale")]
    [InlineData(typeof(TrashUnavailableException), "trash_unavailable")]
    [InlineData(typeof(PermanentDeleteConfirmationRequiredException), "permanent_delete_confirmation_required")]
    public void Stable_exceptions_expose_expected_code(Type exceptionType, string expectedCode)
    {
        var exception = (FileOperationException)Activator.CreateInstance(exceptionType)!;

        Assert.Equal(expectedCode, exception.Code);
        Assert.False(string.IsNullOrWhiteSpace(exception.PublicDetail));
    }
}
