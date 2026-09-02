using System.Text;
using System.Text.Json;
using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Files;
using ReachCommander.Application.TextEncodings;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.TextEncodings;

public sealed class TextEncodingPlannerTests
{
    [Fact]
    public async Task Preview_creates_safe_plan_for_mixed_high_and_low_confidence_files()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteWindows1250("TV/episode.srt", "Bună, ştii, ţară.\r\n");
        fixture.WriteUtf8("TV/notes.txt", "UTF-8 notes 😀\r\n");

        var preview = await fixture.Planner.PreviewAsync(new(
            "media",
            ["/TV/episode.srt", "/TV/notes.txt"],
            TextEncodingKind.Auto,
            TextEncodingKind.Utf8),
            CancellationToken.None);

        Assert.True(preview.CanExecute);
        Assert.Equal(2, preview.Rows.Count);
        Assert.Equal(1, preview.ReadyCount);
        Assert.Equal(1, preview.WarningCount);
        Assert.Equal(0, preview.InvalidCount);
        Assert.DoesNotContain(fixture.SourceRoot, JsonSerializer.Serialize(preview));
        Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(10), preview.ExpiresAt);

        var stored = fixture.PlanStore.Get(preview.PlanId);
        Assert.Equal(2, stored.Entries.Count);
        Assert.All(stored.Entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Fingerprint.Sha256)));
    }

    [Fact]
    public async Task Preview_rejects_read_only_source()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/episode.srt", "subtitle");

        await Assert.ThrowsAsync<OperationSourceReadOnlyException>(async () =>
            await fixture.CreatePlanner(sourceReadOnly: true).PreviewAsync(new(
                "media",
                ["/TV/episode.srt"],
                TextEncodingKind.Auto,
                TextEncodingKind.Utf8),
                CancellationToken.None));
    }

    [Fact]
    public async Task Preview_preserves_missing_path_as_existing_file_access_error()
    {
        using var fixture = new TextEncodingTestFixture();

        await Assert.ThrowsAsync<EntryNotFoundException>(async () =>
            await fixture.Planner.PreviewAsync(new(
                "media",
                ["/TV/missing.srt"],
                TextEncodingKind.Auto,
                TextEncodingKind.Utf8),
                CancellationToken.None));
    }

    [Fact]
    public async Task Preview_rejects_more_than_one_hundred_files()
    {
        using var fixture = new TextEncodingTestFixture();
        var request = new TextEncodingPreviewRequest(
            "media",
            Enumerable.Range(1, 101).Select(index => $"/TV/{index}.srt").ToArray(),
            TextEncodingKind.Auto,
            TextEncodingKind.Utf8);

        var error = await Assert.ThrowsAsync<TextEncodingException>(async () =>
            await fixture.Planner.PreviewAsync(request, CancellationToken.None));

        Assert.Equal("text_encoding_invalid_request", error.Code);
    }

    [Fact]
    public async Task Preview_marks_file_larger_than_thirty_two_mebibytes_invalid_without_loading_it()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.CreateSizedFile("TV/large.srt", (32L * 1024 * 1024) + 1);

        var preview = await PreviewSingleAsync(fixture, "/TV/large.srt");

        var row = Assert.Single(preview.Rows);
        Assert.Equal(TextEncodingPreviewStatus.Invalid, row.Status);
        Assert.Equal("text_file_too_large", row.Code);
        Assert.False(preview.CanExecute);
    }

    [Fact]
    public async Task Preview_marks_directory_invalid()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.CreateDirectory("TV/folder.srt");

        var preview = await PreviewSingleAsync(fixture, "/TV/folder.srt");

        Assert.Equal("text_file_not_regular", Assert.Single(preview.Rows).Code);
    }

    [Fact]
    public async Task Preview_marks_symbolic_link_invalid()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/link.srt", "subtitle");
        fixture.MarkAsSymbolicLink("TV/link.srt");

        var preview = await PreviewSingleAsync(fixture, "/TV/link.srt");

        Assert.Equal("text_symbolic_link_rejected", Assert.Single(preview.Rows).Code);
    }

    [Fact]
    public async Task Preview_marks_unsupported_extension_invalid()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/episode.xml", "subtitle");

        var preview = await PreviewSingleAsync(fixture, "/TV/episode.xml");

        Assert.Equal("unsupported_text_extension", Assert.Single(preview.Rows).Code);
    }

    [Fact]
    public async Task Preview_marks_binary_subtitle_invalid()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteBytes("TV/episode.sub", [0x00, 0x01, 0x02, 0x03, 0x04]);

        var preview = await PreviewSingleAsync(fixture, "/TV/episode.sub");

        Assert.Equal("text_binary_content", Assert.Single(preview.Rows).Code);
    }

    [Fact]
    public async Task Preview_honors_manual_legacy_source_override()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteWindows1250("TV/episode.srt", "Bună, ştii, ţară.\r\n");

        var preview = await fixture.Planner.PreviewAsync(new(
            "media",
            ["/TV/episode.srt"],
            TextEncodingKind.Windows1250,
            TextEncodingKind.Utf8),
            CancellationToken.None);

        var row = Assert.Single(preview.Rows);
        Assert.Equal(TextEncodingPreviewStatus.Ready, row.Status);
        Assert.Equal(TextEncodingConfidence.High, row.Confidence);
        Assert.Equal(TextEncodingKind.Windows1250, row.DetectedSourceEncoding);
        Assert.Contains("Bună", row.PreviewText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plan_expires_after_ten_minutes()
    {
        using var fixture = new TextEncodingTestFixture();
        fixture.WriteUtf8("TV/episode.srt", "subtitle");
        var preview = await PreviewSingleAsync(fixture, "/TV/episode.srt");

        fixture.Clock.Advance(TimeSpan.FromMinutes(10));

        var error = Assert.Throws<TextEncodingException>(() => fixture.PlanStore.Get(preview.PlanId));
        Assert.Equal("text_encoding_plan_expired", error.Code);
    }

    private static ValueTask<TextEncodingPreview> PreviewSingleAsync(
        TextEncodingTestFixture fixture,
        string path) => fixture.Planner.PreviewAsync(new(
            "media",
            [path],
            TextEncodingKind.Auto,
            TextEncodingKind.Utf8),
            CancellationToken.None);
}
