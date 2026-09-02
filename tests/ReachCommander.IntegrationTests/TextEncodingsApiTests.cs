using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace ReachCommander.IntegrationTests;

public sealed class TextEncodingsApiTests(ReachCommanderApiFactory factory)
    : IClassFixture<ReachCommanderApiFactory>
{
    [Fact]
    public async Task Preview_execute_and_poll_preserve_exact_original_and_return_logical_paths_only()
    {
        var (logicalDirectory, physicalDirectory) = CreateCaseDirectory(factory);
        var original = StrictWindows(1250).GetBytes("Bună, ştii, ţară.\r\n");
        File.WriteAllBytes(Path.Combine(physicalDirectory, "episode.srt"), original);
        using var client = factory.CreateClient();

        var previewResponse = await client.PostAsJsonAsync("/api/text-encodings/preview", new
        {
            sourceId = "media",
            filePaths = new[] { $"{logicalDirectory}/episode.srt" },
            sourceEncoding = "auto",
            outputEncoding = "utf8",
        });
        var previewBody = await previewResponse.Content.ReadAsStringAsync();
        var preview = await previewResponse.Content.ReadFromJsonAsync<PreviewResponse>();

        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        Assert.True(preview!.CanExecute);
        Assert.Equal("warning", Assert.Single(preview.Rows).Status);
        Assert.DoesNotContain(factory.MediaRoot, previewBody, StringComparison.OrdinalIgnoreCase);

        var start = await client.PostAsync(
            $"/api/text-encodings/{preview.PlanId}/execute",
            content: null);
        var queued = await start.Content.ReadFromJsonAsync<OperationResponse>();
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var operation = await PollTerminalAsync(client, queued!);
        var operationJson = System.Text.Json.JsonSerializer.Serialize(operation);

        Assert.Equal("completed", operation.State);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(physicalDirectory, "episode_original.srt")));
        Assert.Equal(
            "Bună, ştii, ţară.\r\n",
            File.ReadAllText(Path.Combine(physicalDirectory, "episode.srt"), new UTF8Encoding(false, true)));
        Assert.DoesNotContain(factory.MediaRoot, operationJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"{logicalDirectory}/episode_original.srt", Assert.Single(operation.Rows).BackupPath);
    }

    [Fact]
    public async Task Preview_returns_mixed_valid_and_invalid_rows_without_mutating_files()
    {
        var (logicalDirectory, physicalDirectory) = CreateCaseDirectory(factory);
        File.WriteAllText(Path.Combine(physicalDirectory, "notes.txt"), "notes");
        File.WriteAllText(Path.Combine(physicalDirectory, "markup.xml"), "<root />");
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/text-encodings/preview", new
        {
            sourceId = "media",
            filePaths = new[] { $"{logicalDirectory}/notes.txt", $"{logicalDirectory}/markup.xml" },
            sourceEncoding = "auto",
            outputEncoding = "utf8",
        });
        var preview = await response.Content.ReadFromJsonAsync<PreviewResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(preview!.CanExecute);
        Assert.Equal(1, preview.InvalidCount);
        Assert.Equal("unsupported_text_extension", preview.Rows.Single(row => row.Status == "invalid").Code);
        Assert.Equal("notes", File.ReadAllText(Path.Combine(physicalDirectory, "notes.txt")));
    }

    [Theory]
    [InlineData("archive", "/episode.srt", HttpStatusCode.Forbidden, "source_read_only")]
    [InlineData("media", "/../episode.srt", HttpStatusCode.BadRequest, "invalid_path")]
    public async Task Preview_enforces_source_and_path_boundaries(
        string sourceId,
        string logicalPath,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        if (sourceId == "archive")
        {
            File.WriteAllText(Path.Combine(factory.ArchiveRoot, "episode.srt"), "subtitle");
        }

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/text-encodings/preview", new
        {
            sourceId,
            filePaths = new[] { logicalPath },
            sourceEncoding = "auto",
            outputEncoding = "utf8",
        });
        var body = await response.Content.ReadAsStringAsync();
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, problem!.Code);
        Assert.DoesNotContain(factory.WorkspaceRoot, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_marks_file_changed_after_preview_as_stale()
    {
        var (logicalDirectory, physicalDirectory) = CreateCaseDirectory(factory);
        var path = Path.Combine(physicalDirectory, "episode.srt");
        File.WriteAllText(path, "previewed");
        using var client = factory.CreateClient();
        var preview = await PreviewSingleAsync(client, logicalDirectory, "episode.srt");
        File.WriteAllText(path, "changed after preview");

        var start = await client.PostAsync($"/api/text-encodings/{preview.PlanId}/execute", null);
        var operation = await PollTerminalAsync(
            client,
            (await start.Content.ReadFromJsonAsync<OperationResponse>())!);

        Assert.Equal("completedWithErrors", operation.State);
        Assert.Equal("skipped", Assert.Single(operation.Rows).Result);
        Assert.Equal("text_file_stale", Assert.Single(operation.Rows).Code);
        Assert.False(File.Exists(Path.Combine(physicalDirectory, "episode_original.srt")));
    }

    [Fact]
    public async Task Expired_plan_and_unknown_operation_use_stable_problem_details()
    {
        var (logicalDirectory, physicalDirectory) = CreateCaseDirectory(factory);
        File.WriteAllText(Path.Combine(physicalDirectory, "episode.srt"), "subtitle");
        using var client = factory.CreateClient();
        var preview = await PreviewSingleAsync(client, logicalDirectory, "episode.srt");
        factory.AdvanceTime(TimeSpan.FromMinutes(10));

        var expired = await client.PostAsync($"/api/text-encodings/{preview.PlanId}/execute", null);
        var expiredProblem = await expired.Content.ReadFromJsonAsync<ProblemResponse>();
        var missing = await client.GetAsync($"/api/text-encodings/operations/{Guid.NewGuid()}");
        var missingProblem = await missing.Content.ReadFromJsonAsync<ProblemResponse>();
        factory.ResetTime();

        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Equal("text_encoding_plan_expired", expiredProblem!.Code);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("text_encoding_operation_not_found", missingProblem!.Code);
    }

    [Fact]
    public async Task Text_encoding_policy_limits_requests_per_ip()
    {
        await using var isolatedFactory = new ReachCommanderApiFactory();
        var directory = CreateCaseDirectory(isolatedFactory);
        File.WriteAllText(Path.Combine(directory.PhysicalPath, "episode.srt"), "subtitle");
        using var client = isolatedFactory.CreateClient();
        HttpResponseMessage? response = null;
        for (var request = 0; request < 21; request++)
        {
            response?.Dispose();
            response = await client.PostAsJsonAsync("/api/text-encodings/preview", new
            {
                sourceId = "media",
                filePaths = new[] { $"{directory.LogicalPath}/episode.srt" },
                sourceEncoding = "auto",
                outputEncoding = "utf8",
            });
        }

        using (response)
        {
            var problem = await response!.Content.ReadFromJsonAsync<ProblemResponse>();
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal("text_encoding_rate_limited", problem!.Code);
        }
    }

    private static async Task<PreviewResponse> PreviewSingleAsync(
        HttpClient client,
        string logicalDirectory,
        string fileName)
    {
        var response = await client.PostAsJsonAsync("/api/text-encodings/preview", new
        {
            sourceId = "media",
            filePaths = new[] { $"{logicalDirectory}/{fileName}" },
            sourceEncoding = "auto",
            outputEncoding = "utf8",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PreviewResponse>())!;
    }

    private static async Task<OperationResponse> PollTerminalAsync(
        HttpClient client,
        OperationResponse operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var current = operation;
        while (current.State is "queued" or "running" or "cancelRequested")
        {
            await Task.Delay(25, timeout.Token);
            current = (await client.GetFromJsonAsync<OperationResponse>(
                $"/api/text-encodings/operations/{current.OperationId}",
                timeout.Token))!;
        }

        return current;
    }

    private static (string LogicalPath, string PhysicalPath) CreateCaseDirectory(
        ReachCommanderApiFactory targetFactory)
    {
        var name = $"text-encoding-{Guid.NewGuid():N}";
        var physicalPath = Path.Combine(targetFactory.MediaRoot, name);
        Directory.CreateDirectory(physicalPath);
        return ($"/{name}", physicalPath);
    }

    private static Encoding StrictWindows(int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            codePage,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    private sealed record PreviewRowResponse(
        string FilePath,
        string Status,
        string? Code);

    private sealed record PreviewResponse(
        Guid PlanId,
        IReadOnlyList<PreviewRowResponse> Rows,
        int InvalidCount,
        bool CanExecute);

    private sealed record OperationRowResponse(
        string FilePath,
        string? BackupPath,
        string Result,
        string? Code);

    private sealed record OperationResponse(
        Guid OperationId,
        string State,
        IReadOnlyList<OperationRowResponse> Rows);

    private sealed record ProblemResponse(string Code);
}
