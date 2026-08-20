using System.Net;
using System.Net.Http.Json;
using ReachCommander.Application.BatchRenames;

namespace ReachCommander.IntegrationTests;

public sealed class BatchRenamesApiTests(ReachCommanderApiFactory factory)
    : IClassFixture<ReachCommanderApiFactory>
{
    [Fact]
    public async Task Preview_execute_and_undo_return_only_logical_names()
    {
        var (logicalDirectory, physicalDirectory) = CreateCaseDirectory();
        File.WriteAllText(Path.Combine(physicalDirectory, "alpha.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(physicalDirectory, "Drafts"));
        using var client = factory.CreateClient();

        var previewResponse = await client.PostAsJsonAsync("/api/batch-renames/preview", new
        {
            sourceId = "media",
            directoryPath = logicalDirectory,
            entryPaths = new[] { $"{logicalDirectory}/alpha.txt", $"{logicalDirectory}/Drafts" },
            rules = Rules("Archive-[C]", "[E]", counterDigits: 3),
        });
        var previewBody = await previewResponse.Content.ReadAsStringAsync();
        var preview = await previewResponse.Content.ReadFromJsonAsync<PreviewResponse>();

        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        Assert.Equal(["Archive-001.txt", "Archive-002"], preview!.Rows.Select(row => row.NewName));
        Assert.True(preview.CanExecute);
        Assert.DoesNotContain(factory.MediaRoot, previewBody, StringComparison.OrdinalIgnoreCase);

        var execute = await client.PostAsync(
            $"/api/batch-renames/{preview.PlanId}/execute",
            content: null);
        var executeBody = await execute.Content.ReadAsStringAsync();
        var operation = await execute.Content.ReadFromJsonAsync<OperationResponse>();
        Assert.Equal(HttpStatusCode.OK, execute.StatusCode);
        Assert.True(operation!.UndoAvailable);
        Assert.True(File.Exists(Path.Combine(physicalDirectory, "Archive-001.txt")));
        Assert.DoesNotContain(factory.MediaRoot, executeBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".reachcommander-rename-", executeBody, StringComparison.OrdinalIgnoreCase);

        var undo = await client.PostAsync(
            $"/api/batch-renames/{operation.OperationId}/undo",
            content: null);
        var undoResult = await undo.Content.ReadFromJsonAsync<OperationResponse>();
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal("undone", undoResult!.Status);
        Assert.True(File.Exists(Path.Combine(physicalDirectory, "alpha.txt")));
        Assert.True(Directory.Exists(Path.Combine(physicalDirectory, "Drafts")));
    }

    [Theory]
    [InlineData("archive", HttpStatusCode.Forbidden, "source_read_only")]
    [InlineData("usb", HttpStatusCode.ServiceUnavailable, "source_unavailable")]
    public async Task Preview_enforces_source_policy_without_mutation(
        string sourceId,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        var fileName = $"rename-policy-{Guid.NewGuid():N}.txt";
        if (sourceId == "archive")
        {
            File.WriteAllText(Path.Combine(factory.ArchiveRoot, fileName), "archive");
        }

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/batch-renames/preview", new
        {
            sourceId,
            directoryPath = "/",
            entryPaths = new[] { $"/{fileName}" },
            rules = Rules("renamed", "[E]"),
        });
        var body = await response.Content.ReadAsStringAsync();
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, problem!.Code);
        Assert.DoesNotContain(factory.WorkspaceRoot, body, StringComparison.OrdinalIgnoreCase);
        if (sourceId == "archive")
        {
            Assert.True(File.Exists(Path.Combine(factory.ArchiveRoot, fileName)));
        }
    }

    [Fact]
    public async Task Destination_conflict_returns_a_non_executable_preview_without_mutation()
    {
        var (logicalDirectory, physicalDirectory) = CreateCaseDirectory();
        File.WriteAllText(Path.Combine(physicalDirectory, "alpha.txt"), "alpha");
        File.WriteAllText(Path.Combine(physicalDirectory, "taken.txt"), "taken");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/batch-renames/preview", new
        {
            sourceId = "media",
            directoryPath = logicalDirectory,
            entryPaths = new[] { $"{logicalDirectory}/alpha.txt" },
            rules = Rules("taken", "txt"),
        });
        var preview = await response.Content.ReadFromJsonAsync<PreviewResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(preview!.CanExecute);
        Assert.Equal("conflict", Assert.Single(preview.Rows).Status);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(physicalDirectory, "alpha.txt")));
        Assert.Equal("taken", File.ReadAllText(Path.Combine(physicalDirectory, "taken.txt")));
    }

    [Fact]
    public async Task Invalid_rule_returns_safe_bad_request()
    {
        var (logicalDirectory, physicalDirectory) = CreateCaseDirectory();
        File.WriteAllText(Path.Combine(physicalDirectory, "alpha.txt"), "alpha");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/batch-renames/preview", new
        {
            sourceId = "media",
            directoryPath = logicalDirectory,
            entryPaths = new[] { $"{logicalDirectory}/alpha.txt" },
            rules = Rules("[N", "[E]"),
        });
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_rename_rule", problem!.Code);
    }

    [Fact]
    public async Task Execute_rejects_expired_and_stale_plans_without_mutation()
    {
        var (expiredLogical, expiredPhysical) = CreateCaseDirectory();
        File.WriteAllText(Path.Combine(expiredPhysical, "expired.txt"), "expired");
        using var client = factory.CreateClient();
        var expiredPreview = await PreviewSingle(
            client,
            expiredLogical,
            "expired.txt",
            Rules("renamed", "txt"));
        factory.AdvanceTime(TimeSpan.FromMinutes(11));

        var expired = await client.PostAsync(
            $"/api/batch-renames/{expiredPreview.PlanId}/execute",
            content: null);
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Equal(
            "rename_plan_expired",
            (await expired.Content.ReadFromJsonAsync<ProblemResponse>())!.Code);
        Assert.True(File.Exists(Path.Combine(expiredPhysical, "expired.txt")));

        factory.ResetTime();
        var (staleLogical, stalePhysical) = CreateCaseDirectory();
        File.WriteAllText(Path.Combine(stalePhysical, "stale.txt"), "original");
        var stalePreview = await PreviewSingle(
            client,
            staleLogical,
            "stale.txt",
            Rules("renamed", "txt"));
        File.WriteAllText(Path.Combine(stalePhysical, "stale.txt"), "changed and longer");

        var stale = await client.PostAsync(
            $"/api/batch-renames/{stalePreview.PlanId}/execute",
            content: null);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(
            "rename_plan_stale",
            (await stale.Content.ReadFromJsonAsync<ProblemResponse>())!.Code);
        Assert.True(File.Exists(Path.Combine(stalePhysical, "stale.txt")));
        Assert.False(File.Exists(Path.Combine(stalePhysical, "renamed.txt")));
    }

    [Fact]
    public async Task Unknown_api_route_remains_a_json_404()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/batch-renames/not-a-route", content: null);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("route_not_found", problem!.Code);
    }

    private async Task<PreviewResponse> PreviewSingle(
        HttpClient client,
        string logicalDirectory,
        string fileName,
        RequestRules rules)
    {
        var response = await client.PostAsJsonAsync("/api/batch-renames/preview", new
        {
            sourceId = "media",
            directoryPath = logicalDirectory,
            entryPaths = new[] { $"{logicalDirectory}/{fileName}" },
            rules,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PreviewResponse>())!;
    }

    private (string LogicalPath, string PhysicalPath) CreateCaseDirectory()
    {
        var name = $"rename-{Guid.NewGuid():N}";
        var physicalPath = Path.Combine(factory.MediaRoot, name);
        Directory.CreateDirectory(physicalPath);
        return ($"/{name}", physicalPath);
    }

    private static RequestRules Rules(
        string nameMask,
        string extensionMask,
        int counterDigits = 1) => new(
            nameMask,
            extensionMask,
            SearchFor: string.Empty,
            ReplaceWith: string.Empty,
            UseRegex: false,
            MatchCase: false,
            ReplaceInExtension: false,
            BatchRenameCaseMode.Unchanged,
            CounterStart: 1,
            CounterStep: 1,
            counterDigits);

    private sealed record RequestRules(
        string NameMask,
        string ExtensionMask,
        string SearchFor,
        string ReplaceWith,
        bool UseRegex,
        bool MatchCase,
        bool ReplaceInExtension,
        BatchRenameCaseMode CaseMode,
        int CounterStart,
        int CounterStep,
        int CounterDigits);

    private sealed record PreviewRowResponse(
        string NewName,
        string Status);

    private sealed record PreviewResponse(
        Guid PlanId,
        IReadOnlyList<PreviewRowResponse> Rows,
        bool CanExecute);

    private sealed record OperationResponse(
        Guid OperationId,
        string Status,
        bool UndoAvailable);

    private sealed record ProblemResponse(string Code);
}
