using System.Net;
using System.Net.Http.Json;

namespace ReachCommander.IntegrationTests;

public sealed class UploadsApiTests(ReachCommanderApiFactory factory)
    : IClassFixture<ReachCommanderApiFactory>
{
    [Fact]
    public async Task Limits_returns_the_effective_safe_configuration()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/uploads/limits");
        var limits = await response.Content.ReadFromJsonAsync<UploadLimitsResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new UploadLimitsResponse(8, 12, 2), limits);
    }

    [Fact]
    public async Task Uploads_multiple_files_and_returns_safe_logical_results()
    {
        var (logicalPath, physicalPath) = CreateDestination();
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("one"u8.ToArray()), "files", "one.txt");
        content.Add(new ByteArrayContent([]), "files", "empty.bin");
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/uploads?sourceId=media&path={Uri.EscapeDataString(logicalPath)}",
            content);
        var body = await response.Content.ReadAsStringAsync();
        var result = await response.Content.ReadFromJsonAsync<UploadResponse>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(result);
        Assert.Equal(2, result.UploadedCount);
        Assert.Equal(3, result.TotalBytes);
        Assert.Equal("one", File.ReadAllText(Path.Combine(physicalPath, "one.txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(physicalPath, "empty.bin")).Length);
        Assert.DoesNotContain(factory.MediaRoot, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".partial", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Existing_name_rejects_the_complete_batch()
    {
        var (logicalPath, physicalPath) = CreateDestination();
        File.WriteAllText(Path.Combine(physicalPath, "existing.txt"), "original");
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("new"u8.ToArray()), "files", "another.txt");
        content.Add(new ByteArrayContent("replace"u8.ToArray()), "files", "EXISTING.TXT");
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/uploads?sourceId=media&path={Uri.EscapeDataString(logicalPath)}",
            content);
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("upload_name_conflict", problem?.Code);
        Assert.Equal("original", File.ReadAllText(Path.Combine(physicalPath, "existing.txt")));
        Assert.False(File.Exists(Path.Combine(physicalPath, "another.txt")));
    }

    [Theory]
    [InlineData("archive", "/", HttpStatusCode.Forbidden, "source_read_only")]
    [InlineData("missing", "/", HttpStatusCode.NotFound, "source_not_found")]
    [InlineData("usb", "/", HttpStatusCode.ServiceUnavailable, "source_unavailable")]
    public async Task Source_policy_failures_use_safe_problem_details(
        string sourceId,
        string path,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        using var content = OneFile("one.txt", "one"u8.ToArray());
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            $"/api/uploads?sourceId={sourceId}&path={Uri.EscapeDataString(path)}",
            content);
        var body = await response.Content.ReadAsStringAsync();
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, problem?.Code);
        Assert.DoesNotContain(factory.WorkspaceRoot, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_multipart_and_empty_requests_are_rejected()
    {
        var (logicalPath, _) = CreateDestination();
        using var client = factory.CreateClient();

        using var nonMultipart = await client.PostAsync(
            $"/api/uploads?sourceId=media&path={Uri.EscapeDataString(logicalPath)}",
            new StringContent("not multipart"));
        using var emptyContent = new MultipartFormDataContent();
        using var empty = await client.PostAsync(
            $"/api/uploads?sourceId=media&path={Uri.EscapeDataString(logicalPath)}",
            emptyContent);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, nonMultipart.StatusCode);
        Assert.Equal("upload_unsupported_media_type", (await nonMultipart.Content.ReadFromJsonAsync<ProblemResponse>())?.Code);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("upload_empty", (await empty.Content.ReadFromJsonAsync<ProblemResponse>())?.Code);
    }

    [Fact]
    public async Task Invalid_filename_and_configured_limits_are_enforced()
    {
        var (logicalPath, physicalPath) = CreateDestination();
        using var client = factory.CreateClient();

        using var invalid = await client.PostAsync(
            $"/api/uploads?sourceId=media&path={Uri.EscapeDataString(logicalPath)}",
            OneFile("../escape.txt", "x"u8.ToArray()));
        using var tooLarge = await client.PostAsync(
            $"/api/uploads?sourceId=media&path={Uri.EscapeDataString(logicalPath)}",
            OneFile("large.bin", new byte[9]));
        using var tooManyContent = new MultipartFormDataContent();
        tooManyContent.Add(new ByteArrayContent([1]), "files", "one.bin");
        tooManyContent.Add(new ByteArrayContent([2]), "files", "two.bin");
        tooManyContent.Add(new ByteArrayContent([3]), "files", "three.bin");
        using var tooMany = await client.PostAsync(
            $"/api/uploads?sourceId=media&path={Uri.EscapeDataString(logicalPath)}",
            tooManyContent);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("upload_name_invalid", (await invalid.Content.ReadFromJsonAsync<ProblemResponse>())?.Code);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal("upload_file_too_large", (await tooLarge.Content.ReadFromJsonAsync<ProblemResponse>())?.Code);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooMany.StatusCode);
        Assert.Equal("upload_too_many_files", (await tooMany.Content.ReadFromJsonAsync<ProblemResponse>())?.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(physicalPath));
    }

    private (string LogicalPath, string PhysicalPath) CreateDestination()
    {
        var name = $"Upload-{Guid.NewGuid():N}";
        var physicalPath = Path.Combine(factory.MediaRoot, name);
        Directory.CreateDirectory(physicalPath);
        return ($"/{name}", physicalPath);
    }

    private static MultipartFormDataContent OneFile(string name, byte[] bytes)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes), "files", name);
        return content;
    }

    private sealed record UploadLimitsResponse(
        long MaxFileBytes,
        long MaxBatchBytes,
        int MaxFilesPerBatch);

    private sealed record UploadResponse(
        int UploadedCount,
        long TotalBytes,
        UploadedFileResponse[] Files);

    private sealed record UploadedFileResponse(string Name, string RelativePath, long Size);

    private sealed record ProblemResponse(
        string Type,
        string Title,
        int Status,
        string Detail,
        string Code);
}
