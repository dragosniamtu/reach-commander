using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReachCommander.Application.Archives;

namespace ReachCommander.IntegrationTests;

public sealed class ArchiveExtractionsApiTests(ReachCommanderApiFactory factory)
    : IClassFixture<ReachCommanderApiFactory>
{
    [Fact]
    public async Task Preview_execute_and_status_use_safe_logical_contracts_and_one_operation()
    {
        factory.ResetArchiveWorker();
        factory.BlockArchiveExtraction();
        using var client = factory.CreateClient();

        var previewResponse = await client.PostAsJsonAsync(
            "/api/archive-extractions/preview",
            ExactPreviewRequest());
        var previewRaw = await previewResponse.Content.ReadAsStringAsync();
        var preview = JsonSerializer.Deserialize<PreviewResponse>(previewRaw, JsonOptions);

        Assert.True(
            previewResponse.StatusCode == HttpStatusCode.OK,
            $"Expected 200 OK but received {(int)previewResponse.StatusCode}: {previewRaw}");
        Assert.NotNull(preview);
        Assert.Equal("sevenZip", preview.Format);
        Assert.Equal(1, preview.VolumeCount);
        Assert.Equal(["2025"], preview.SelectedRoots);
        Assert.Equal(1, preview.FileCount);
        Assert.Equal(1, preview.DirectoryCount);
        Assert.Equal(1, preview.TotalExtractedBytes);
        Assert.Equal("media", preview.DestinationSourceId);
        Assert.Equal("/Photos", preview.DestinationPath);
        Assert.Empty(preview.Conflicts);
        Assert.Empty(preview.Violations);
        Assert.True(preview.CanExecute);
        AssertSafe(previewRaw);

        var execute = await client.PostAsync(
            $"/api/archive-extractions/{preview.PlanId}/execute",
            content: null);
        var accepted = await execute.Content.ReadFromJsonAsync<OperationResponse>();

        Assert.Equal(HttpStatusCode.Accepted, execute.StatusCode);
        Assert.NotNull(accepted);
        Assert.Equal(
            $"/api/archive-extractions/{accepted.OperationId}",
            execute.Headers.Location?.AbsolutePath);
        Assert.Contains(accepted.State, new[] { "queued", "extracting" });
        Assert.True(accepted.CanCancel);
        Assert.Equal("notRequired", accepted.CompensationState);
        Assert.Null(accepted.ErrorCode);
        await WaitForExtractionStart();

        var repeated = await client.PostAsync(
            $"/api/archive-extractions/{preview.PlanId}/execute",
            content: null);
        var repeatedOperation = await repeated.Content.ReadFromJsonAsync<OperationResponse>();
        Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
        Assert.Equal(accepted.OperationId, repeatedOperation!.OperationId);
        Assert.Equal(1, factory.ArchiveExtractionCount);

        factory.ReleaseArchiveExtraction();
        var terminal = await PollTerminal(client, accepted.OperationId);
        Assert.Equal("completed", terminal.State);
        Assert.Equal(1, terminal.CompletedFiles);
        Assert.Equal(1, terminal.TotalFiles);
        Assert.Equal(1, terminal.ExtractedBytes);
        Assert.Equal(100, terminal.Percent);
        Assert.False(terminal.CanCancel);
        Assert.True(File.Exists(Path.Combine(factory.MediaRoot, "Photos", "2025", "photo.jpg")));

        var completedRepeat = await client.PostAsync(
            $"/api/archive-extractions/{preview.PlanId}/execute",
            content: null);
        var completedRepeatBody = await completedRepeat.Content.ReadFromJsonAsync<OperationResponse>();
        Assert.Equal(accepted.OperationId, completedRepeatBody!.OperationId);
        Assert.Equal("completed", completedRepeatBody.State);
        Assert.Equal(1, factory.ArchiveExtractionCount);
    }

    [Fact]
    public async Task Conflict_preview_is_safe_and_execute_does_not_start_worker()
    {
        factory.ResetArchiveWorker();
        var destinationName = $"conflict-{Guid.NewGuid():N}";
        var physicalDestination = Path.Combine(factory.MediaRoot, destinationName);
        Directory.CreateDirectory(Path.Combine(physicalDestination, "Family"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/archive-extractions/preview", new
        {
            sourceId = "downloads",
            archivePath = "/sample.zip",
            internalDirectory = "/",
            entryPaths = Array.Empty<string>(),
            extractAll = true,
            destinationSourceId = "media",
            destinationPath = $"/{destinationName}",
        });
        var preview = await response.Content.ReadFromJsonAsync<PreviewResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(preview!.CanExecute);
        Assert.Contains(preview.Conflicts, issue => issue.Code == "archive_destination_conflict");

        var execute = await client.PostAsync(
            $"/api/archive-extractions/{preview.PlanId}/execute",
            content: null);
        var raw = await execute.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(raw);

        Assert.Equal(HttpStatusCode.Conflict, execute.StatusCode);
        Assert.Equal("archive_destination_conflict", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, factory.ArchiveExtractionCount);
        AssertSafe(raw);
    }

    [Fact]
    public async Task Cancel_is_idempotent_and_returns_latest_operation()
    {
        factory.ResetArchiveWorker();
        factory.BlockArchiveExtraction();
        using var client = factory.CreateClient();
        var preview = await PreviewExact(client, destinationPath: $"/cancel-{Guid.NewGuid():N}");
        var execute = await client.PostAsync(
            $"/api/archive-extractions/{preview.PlanId}/execute",
            content: null);
        var operation = await execute.Content.ReadFromJsonAsync<OperationResponse>();

        var first = await client.PostAsync(
            $"/api/archive-extractions/{operation!.OperationId}/cancel",
            content: null);
        var second = await client.PostAsync(
            $"/api/archive-extractions/{operation.OperationId}/cancel",
            content: null);
        factory.ReleaseArchiveExtraction();
        var terminal = await PollTerminal(client, operation.OperationId);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("cancelled", terminal.State);
        Assert.False(terminal.CanCancel);
    }

    [Fact]
    public async Task Execute_distinguishes_an_expired_unexecuted_plan_from_a_random_id()
    {
        factory.ResetTime();
        try
        {
            var destinationPath = $"/expired-{Guid.NewGuid():N}";
            Directory.CreateDirectory(Path.Combine(factory.MediaRoot, destinationPath.Trim('/')));
            using var client = factory.CreateClient();
            var preview = await PreviewExact(client, destinationPath);
            factory.AdvanceTime(TimeSpan.FromMinutes(10));

            var expired = await client.PostAsync(
                $"/api/archive-extractions/{preview.PlanId}/execute",
                content: null);
            var missing = await client.PostAsync(
                "/api/archive-extractions/not-a-plan/execute",
                content: null);

            Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
            Assert.Equal(
                "archive_plan_expired",
                JsonDocument.Parse(await expired.Content.ReadAsStringAsync())
                    .RootElement.GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(
                "archive_plan_not_found",
                JsonDocument.Parse(await missing.Content.ReadAsStringAsync())
                    .RootElement.GetProperty("code").GetString());
        }
        finally
        {
            factory.ResetTime();
        }
    }

    [Theory]
    [InlineData("{\"sourceId\":\"downloads\",\"archivePath\":\"/sample.zip\",\"internalDirectory\":\"/\",\"entryPaths\":[],\"extractAll\":false,\"destinationSourceId\":\"media\",\"destinationPath\":\"/\"}")]
    [InlineData("{\"sourceId\":\"downloads\",\"archivePath\":\"/sample.zip\",\"internalDirectory\":\"/\",\"entryPaths\":[\"/Family\"],\"extractAll\":true,\"destinationSourceId\":\"media\",\"destinationPath\":\"/\"}")]
    [InlineData("{\"sourceId\":\"downloads\",\"archivePath\":\"/sample.zip\",\"internalDirectory\":\"/\",\"entryPaths\":[\"/Family\",\"/Family\"],\"extractAll\":false,\"destinationSourceId\":\"media\",\"destinationPath\":\"/\"}")]
    [InlineData("{\"sourceId\":\"downloads\",\"archivePath\":\"/sample.zip\",\"internalDirectory\":\"/\",\"entryPaths\":[],\"extractAll\":true,\"destinationSourceId\":\"media\",\"destinationPath\":\"/\",\"password\":\"secret\"}")]
    public async Task Preview_rejects_invalid_modes_duplicates_and_unknown_properties(string json)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/archive-extractions/preview",
            new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [MemberData(nameof(ArchiveErrors))]
    public async Task Archive_failures_have_exact_safe_problem_details(
        ArchiveException exception,
        HttpStatusCode expectedStatus)
    {
        using var application = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IArchiveExtractionService>();
                services.AddSingleton<IArchiveExtractionService>(new ThrowingExtractionService(exception));
            }));
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/archive-extractions/preview",
            ExactPreviewRequest());
        var raw = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(raw);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal((int)expectedStatus, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(exception.Code, problem.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("detail").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.RootElement.GetProperty("type").GetString()));
        Assert.Equal("/api/archive-extractions/preview", problem.RootElement.GetProperty("instance").GetString());
        AssertSafe(raw);
    }

    [Fact]
    public async Task Preview_rejects_a_body_over_eight_mib_without_inspection()
    {
        factory.ResetArchiveWorker();
        using var client = factory.CreateClient();
        using var content = new ByteArrayContent(new byte[(8 * 1024 * 1024) + 1]);
        content.Headers.ContentType = new("application/json");

        var response = await client.PostAsync("/api/archive-extractions/preview", content);
        var raw = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(raw);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("request_too_large", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, factory.ArchiveInspectionCount);
        AssertSafe(raw);
    }

    [Fact]
    public async Task Server_body_limit_exception_also_maps_to_safe_413_problem_details()
    {
        using var application = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IArchiveExtractionService>();
                services.AddSingleton<IArchiveExtractionService>(new ThrowingExtractionService(
                    new BadHttpRequestException(
                        "Server-specific body limit detail.",
                        StatusCodes.Status413PayloadTooLarge)));
            }));
        using var client = application.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/archive-extractions/preview",
            ExactPreviewRequest());
        var raw = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(raw);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("request_too_large", problem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("server-specific", raw, StringComparison.OrdinalIgnoreCase);
        AssertSafe(raw);
    }

    public static TheoryData<ArchiveException, HttpStatusCode> ArchiveErrors => new()
    {
        { new ArchiveUnsupportedException(), HttpStatusCode.UnsupportedMediaType },
        { new ArchiveInvalidException(), HttpStatusCode.BadRequest },
        { new ArchiveEncryptedException(), HttpStatusCode.UnprocessableEntity },
        { new ArchiveVolumeSecondaryException("/primary.rar"), HttpStatusCode.Conflict },
        { new ArchiveVolumeSetInvalidException(["/part2.rar"]), HttpStatusCode.UnprocessableEntity },
        { new ArchiveEntryUnsafeException(), HttpStatusCode.UnprocessableEntity },
        { new ArchiveLimitExceededException("The archive exceeds a configured limit."), HttpStatusCode.RequestEntityTooLarge },
        { new ArchiveDestinationInvalidException(), HttpStatusCode.BadRequest },
        { new ArchiveDestinationReadOnlyException("archive"), HttpStatusCode.Forbidden },
        { new ArchiveDestinationConflictException(["Family"]), HttpStatusCode.Conflict },
        { new ArchivePlanNotFoundException(), HttpStatusCode.NotFound },
        { new ArchivePlanExpiredException(), HttpStatusCode.Gone },
        { new ArchivePlanStaleException(), HttpStatusCode.Conflict },
        { new ArchiveDestinationChangedException(), HttpStatusCode.Conflict },
        { new ArchiveCapacityReachedException(), HttpStatusCode.TooManyRequests },
        { new ArchiveWorkerFailedException(), HttpStatusCode.InternalServerError },
        { new ArchiveExtractionCancelledException(), (HttpStatusCode)499 },
        { new ArchiveRecoveryRequiredException(["safe.partial"]), HttpStatusCode.InternalServerError },
    };

    private static object ExactPreviewRequest(string destinationPath = "/Photos") => new
    {
        sourceId = "downloads",
        archivePath = "/backups/photos.7z",
        internalDirectory = "/Family",
        entryPaths = new[] { "/Family/2025" },
        extractAll = false,
        destinationSourceId = "media",
        destinationPath,
    };

    private async Task<PreviewResponse> PreviewExact(
        HttpClient client,
        string destinationPath = "/Photos")
    {
        Directory.CreateDirectory(Path.Combine(
            factory.MediaRoot,
            destinationPath.Trim('/').Replace('/', Path.DirectorySeparatorChar)));
        var response = await client.PostAsJsonAsync(
            "/api/archive-extractions/preview",
            ExactPreviewRequest(destinationPath));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PreviewResponse>())!;
    }

    private static async Task<OperationResponse> PollTerminal(HttpClient client, string operationId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var operation = await client.GetFromJsonAsync<OperationResponse>(
                $"/api/archive-extractions/{operationId}");
            if (operation!.State is "completed" or "cancelled" or "failed" or "recoveryRequired")
            {
                return operation;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The archive extraction did not reach a terminal state.");
    }

    private async Task WaitForExtractionStart()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (factory.ArchiveExtractionCount > 0)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The archive extraction worker did not start.");
    }

    private void AssertSafe(string raw)
    {
        Assert.DoesNotContain(factory.WorkspaceRoot, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stderr", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SharpCompress", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stackTrace", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("physicalPath", raw, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record IssueResponse(
        string Code,
        string Message,
        IReadOnlyList<string> LogicalPaths);

    private sealed record PreviewResponse(
        string PlanId,
        DateTimeOffset ExpiresAt,
        string Format,
        int VolumeCount,
        IReadOnlyList<string> SelectedRoots,
        int FileCount,
        int DirectoryCount,
        long? TotalExtractedBytes,
        string DestinationSourceId,
        string DestinationPath,
        IReadOnlyList<IssueResponse> Conflicts,
        IReadOnlyList<IssueResponse> Violations,
        bool CanExecute);

    private sealed record OperationResponse(
        string OperationId,
        string State,
        int CompletedFiles,
        int TotalFiles,
        long ExtractedBytes,
        long? TotalBytes,
        double? Percent,
        string? CurrentEntryName,
        bool CanCancel,
        string CompensationState,
        IReadOnlyList<string> RecoveryNames,
        string? ErrorCode,
        string? ErrorDetail);

    private sealed class ThrowingExtractionService(Exception exception)
        : IArchiveExtractionService
    {
        public ValueTask<ArchiveExtractionPreview> PreviewAsync(
            ArchiveExtractionPreviewRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<ArchiveExtractionPreview>(exception);

        public ValueTask<ArchiveExtractionOperation> ExecuteAsync(
            string planId,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<ArchiveExtractionOperation>(exception);

        public ValueTask<ArchiveExtractionOperation> GetAsync(
            string operationId,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<ArchiveExtractionOperation>(exception);

        public ValueTask<ArchiveExtractionOperation> CancelAsync(
            string operationId,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<ArchiveExtractionOperation>(exception);
    }
}
