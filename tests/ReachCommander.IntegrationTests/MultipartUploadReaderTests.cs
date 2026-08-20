using Microsoft.AspNetCore.Http;
using ReachCommander.Api.Uploads;
using ReachCommander.Infrastructure.Uploads;

namespace ReachCommander.IntegrationTests;

public sealed class MultipartUploadReaderTests
{
    [Fact]
    public async Task Reader_yields_each_section_body_without_buffering_the_batch()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent("one"u8.ToArray()), "files", "one.txt");
        content.Add(new ByteArrayContent([]), "files", "empty.bin");
        await using var body = new MemoryStream();
        await content.CopyToAsync(body);
        body.Position = 0;
        var context = new DefaultHttpContext();
        context.Request.ContentType = content.Headers.ContentType!.ToString();
        context.Request.Body = body;
        var reader = new MultipartUploadReader();
        var observed = new List<(string Name, string Content, long? Length)>();

        await foreach (var part in reader.ReadAsync(
                           context.Request,
                           new UploadOptions
                           {
                               MaxFileBytes = 8,
                               MaxBatchBytes = 12,
                               MaxFilesPerBatch = 2,
                               MaxConcurrentBatches = 1,
                           },
                           CancellationToken.None))
        {
            using var section = new MemoryStream();
            await part.Content.CopyToAsync(section);
            observed.Add((
                part.FileName,
                System.Text.Encoding.UTF8.GetString(section.ToArray()),
                part.DeclaredLength));
        }

        Assert.Equal(2, observed.Count);
        Assert.Equal(("one.txt", "one", (long?)null), observed[0]);
        Assert.Equal(("empty.bin", string.Empty, (long?)null), observed[1]);
    }

    [Fact]
    public async Task Reader_returns_no_parts_for_a_valid_empty_multipart_form()
    {
        using var content = new MultipartFormDataContent();
        await using var body = new MemoryStream();
        await content.CopyToAsync(body);
        body.Position = 0;
        var context = new DefaultHttpContext();
        context.Request.ContentType = content.Headers.ContentType!.ToString();
        context.Request.Body = body;
        var reader = new MultipartUploadReader();
        var observed = new List<string>();

        await foreach (var part in reader.ReadAsync(
                           context.Request,
                           new UploadOptions(),
                           CancellationToken.None))
        {
            observed.Add(part.FileName);
        }

        Assert.Empty(observed);
    }
}
