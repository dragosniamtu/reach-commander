using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Sources;
using ReachCommander.Application.SubtitleGeneration;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.Authentication;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.Infrastructure.Security;
using ReachCommander.Infrastructure.SubtitleGeneration;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.SubtitleGeneration;

public sealed class SubtitleInstallerTests
{
    private const string English = "1\n00:00:01,000 --> 00:00:02,000\nHello there.\n\n2\n00:00:03,000 --> 00:00:04,000\nGood morning.\n";
    private const string Romanian = "1\n00:00:01,000 --> 00:00:02,000\nBună ziua.\n\n2\n00:00:03,000 --> 00:00:04,000\nBună dimineața.\n";

    [Theory]
    [InlineData(true, false, "Hello there.", false)]
    [InlineData(true, true, "Hello there.", true)]
    [InlineData(false, true, "Bună ziua.", false)]
    public async Task Uses_exact_basename_and_romanian_suffix_only_for_bilingual_output(
        bool english, bool romanian, string primaryText, bool separateRomanian)
    {
        using var fixture = new Fixture("Un film [2026] ș.mkv");
        await fixture.Installer.InstallAsync(fixture.Video, english, romanian,
            new(English, romanian ? Romanian : null), default);

        Assert.Contains(primaryText, File.ReadAllText(fixture.Sidecar(".srt")));
        Assert.Equal(separateRomanian, File.Exists(fixture.Sidecar(".ro.srt")));
        Assert.Equal("input video", File.ReadAllText(fixture.Video.PhysicalPath));
        Assert.True(await fixture.Installer.IsAlreadyGeneratedAsync(fixture.Video, english, romanian, default));
    }

    [Fact]
    public async Task Preserves_all_supported_sidecars_without_touching_similarly_named_videos()
    {
        using var fixture = new Fixture();
        string[] suffixes = [".srt", ".en.srt", ".ro.srt", ".eng.forced.ass", ".ro.SDH.ssa", ".en-US.vtt"];
        foreach (var suffix in suffixes)
            File.WriteAllText(fixture.Sidecar(suffix), "original" + suffix);
        File.WriteAllText(fixture.Sidecar(".part2.mkv"), "other video");
        File.WriteAllText(fixture.Sidecar(".part2.srt"), "unrelated part2");
        File.WriteAllText(fixture.Sidecar(".en.mkv"), "other language video");
        File.WriteAllText(fixture.Sidecar(".en.forced.srt"), "unrelated language video");
        File.WriteAllText(fixture.Sidecar(".extraneous.srt"), "unknown suffix");

        await fixture.Installer.InstallAsync(fixture.Video, true, true, new(English, Romanian), default);

        // Movie.en.srt belongs to the distinct Movie.en.mkv and must be left alone.
        var backups = Directory.GetFiles(fixture.Media, "*.bak");
        Assert.Equal(suffixes.Length - 1, backups.Length);
        foreach (var suffix in suffixes.Where(s => s != ".en.srt"))
            Assert.Contains(backups, p => File.ReadAllText(p) == "original" + suffix);
        Assert.Equal("original.en.srt", File.ReadAllText(fixture.Sidecar(".en.srt")));
        Assert.Equal("unrelated part2", File.ReadAllText(fixture.Sidecar(".part2.srt")));
        Assert.Equal("unrelated language video", File.ReadAllText(fixture.Sidecar(".en.forced.srt")));
        Assert.Equal("unknown suffix", File.ReadAllText(fixture.Sidecar(".extraneous.srt")));
    }

    [Fact]
    public async Task Uses_unique_backup_names_on_repeated_installation()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        await fixture.Installer.InstallAsync(fixture.Video, true, false, new(English, null), default);
        var first = Assert.Single(Directory.GetFiles(fixture.Media, "*.bak"));
        await fixture.Installer.InstallAsync(fixture.Video, true, false, new(English, null), default);

        Assert.Equal(2, Directory.GetFiles(fixture.Media, "*.bak").Length);
        Assert.Equal("original", File.ReadAllText(first));
        Assert.Matches(@"\.existing-\d{8}-\d{3,}\.srt\.bak$", first);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\n  \n")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nHello\n\n2\n00:00:00,000 --> 00:00:00,500\nBackwards\n")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nHello\n\n2\n00:00:01,500 --> 00:00:03,000\nOverlapping\n")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nThis subtitle line is much too long to read comfortably.\n")]
    [InlineData("1\n00:00:01,000 --> 00:00:02,000\nOne\nTwo\nThree\n")]
    [InlineData("1\n00:00:02,000 --> 00:00:03,000\nDifferent timing\n")]
    public async Task Invalid_translation_never_touches_originals(string romanian)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        File.WriteAllText(fixture.Sidecar(".ass"), "original ass");

        await Assert.ThrowsAsync<SubtitleGenerationException>(() => fixture.Installer.InstallAsync(
            fixture.Video, true, true, new(English, romanian), default));

        Assert.Equal("original", File.ReadAllText(fixture.Sidecar(".srt")));
        Assert.Equal("original ass", File.ReadAllText(fixture.Sidecar(".ass")));
        Assert.Empty(Directory.GetFiles(fixture.Media, "*.bak"));
        Assert.Empty(Directory.GetFiles(fixture.Media, ".reachcommander-*"));
    }

    [Fact]
    public async Task Rejects_malformed_unicode_before_touching_originals()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        await Assert.ThrowsAsync<SubtitleGenerationException>(() => fixture.Installer.InstallAsync(
            fixture.Video, true, false, new(English.Replace("Hello there.", "\ud800"), null), default));
        Assert.Equal("original", File.ReadAllText(fixture.Sidecar(".srt")));
    }

    [Fact]
    public async Task Rejects_ambiguous_video_stems_without_renaming_sidecars()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".mp4"), "another video");
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        await Assert.ThrowsAsync<SubtitleGenerationException>(() => fixture.Installer.InstallAsync(
            fixture.Video, true, false, new(English, null), default));
        Assert.Equal("original", File.ReadAllText(fixture.Sidecar(".srt")));
    }

    [Fact]
    public async Task Rejects_output_owned_by_another_video_even_when_its_sidecar_does_not_yet_exist()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".ro.mkv"), "another video");
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        await Assert.ThrowsAsync<SubtitleGenerationException>(() => fixture.Installer.InstallAsync(
            fixture.Video, true, true, new(English, Romanian), default));
        Assert.Equal("original", File.ReadAllText(fixture.Sidecar(".srt")));
        Assert.False(File.Exists(fixture.Sidecar(".ro.srt")));
    }

    [Theory]
    [InlineData(".en.ts")]
    [InlineData(".en.wmv")]
    public async Task Never_claims_subtitles_of_video_formats_the_generator_does_not_support(string otherVideo)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(otherVideo), "another video");
        File.WriteAllText(fixture.Sidecar(".en.srt"), "other video's subtitle");
        await fixture.Installer.InstallAsync(fixture.Video, true, false, new(English, null), default);
        Assert.Equal("other video's subtitle", File.ReadAllText(fixture.Sidecar(".en.srt")));
        Assert.Empty(Directory.GetFiles(fixture.Media, "*.bak"));
    }

    [Theory]
    [InlineData("fre")]
    [InlineData("ger")]
    [InlineData("dut")]
    public async Task Preserves_common_bibliographic_language_suffixes(string language)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar($".{language}.sdh.srt"), "original");
        await fixture.Installer.InstallAsync(fixture.Video, true, false, new(English, null), default);
        Assert.Equal("original", File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Media, "*.bak"))));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("fra")]
    [InlineData("es")]
    [InlineData("spa")]
    [InlineData("de")]
    [InlineData("deu")]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hans-CN")]
    public async Task Preserves_language_and_region_sidecars_without_relying_on_installed_cultures(string language)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar($".{language}.sdh.vtt"), "original");
        await fixture.Installer.InstallAsync(fixture.Video, true, false, new(English, null), default);
        Assert.Equal("original", File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Media, "*.bak"))));
    }

    [Fact]
    public async Task Insufficient_free_space_does_not_touch_originals()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        var installer = fixture.CreateInstaller(minimumFreeBytes: long.MaxValue / 2);
        var error = await Assert.ThrowsAsync<SubtitleGenerationException>(() => installer.InstallAsync(
            fixture.Video, true, false, new(English, null), default));
        Assert.Equal("subtitle_insufficient_space", error.Code);
        Assert.Equal("original", File.ReadAllText(fixture.Sidecar(".srt")));
        Assert.Empty(Directory.GetFiles(fixture.Media, "*.partial"));
    }

    [Fact]
    public async Task Installation_waits_for_the_existing_directory_mutation_lock()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        await using var lease = await fixture.MutationLock.AcquireAsync("media", "/", default);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Installer.InstallAsync(
            fixture.Video, true, false, new(English, null), cancellation.Token));
        Assert.Equal("original", File.ReadAllText(fixture.Sidecar(".srt")));
        Assert.Empty(Directory.GetFiles(fixture.Media, "*.partial"));
    }

    [Theory]
    [InlineData("movie.mkv")]
    [InlineData("MOVIE.MKV")]
    public void Rejects_case_aliases_of_video_names(string alias)
    {
        Assert.Throws<SubtitleGenerationException>(() => SubtitleNaming.AssociatedSidecars(
            "Movie.mkv", ["Movie.mkv", alias, "Movie.srt"]));
    }

    [Theory]
    [InlineData("movie.srt")]
    [InlineData("MOVIE.SRT")]
    public void Rejects_case_aliases_of_associated_sidecars(string alias)
    {
        Assert.Throws<SubtitleGenerationException>(() => SubtitleNaming.AssociatedSidecars(
            "Movie.mkv", ["Movie.mkv", "Movie.srt", alias]));
    }

    [Fact]
    public async Task Rejects_read_only_sources_and_changed_video_fingerprints()
    {
        using var fixture = new Fixture();
        fixture.Catalog.Source = fixture.Catalog.Source with { IsReadOnly = true };
        await Assert.ThrowsAsync<SubtitleGenerationException>(() => fixture.Installer.InstallAsync(
            fixture.Video, true, false, new(English, null), default));
        fixture.Catalog.Source = fixture.Catalog.Source with { IsReadOnly = false };
        await File.AppendAllTextAsync(fixture.Video.PhysicalPath, "changed");
        await Assert.ThrowsAsync<SubtitleGenerationException>(() => fixture.Installer.InstallAsync(
            fixture.Video, true, false, new(English, null), default));
        Assert.False(File.Exists(fixture.Sidecar(".srt")));
    }

    [Fact]
    public async Task Receipts_require_current_output_hashes_languages_and_source_identity()
    {
        using var fixture = new Fixture();
        await fixture.Installer.InstallAsync(fixture.Video, true, false, new(English, null), default);
        Assert.True(await fixture.CreateInstaller().IsAlreadyGeneratedAsync(fixture.Video, true, false, default));
        Assert.False(await fixture.Installer.IsAlreadyGeneratedAsync(fixture.Video, false, true, default));
        File.WriteAllText(fixture.Sidecar(".srt"), "user edit");
        Assert.False(await fixture.Installer.IsAlreadyGeneratedAsync(fixture.Video, true, false, default));
        await fixture.Installer.InstallAsync(fixture.Video, true, false, new(English, null), default);
        fixture.Catalog.Source = fixture.Catalog.Source with { RootPath = fixture.Data };
        Assert.False(await fixture.Installer.IsAlreadyGeneratedAsync(fixture.Video, true, false, default));
    }

    [Fact]
    public async Task Publication_failure_rolls_back_all_originals_after_first_output_was_published()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original English");
        File.WriteAllText(fixture.Sidecar(".ro.srt"), "original Romanian");
        File.WriteAllText(fixture.Sidecar(".ass"), "original ASS");
        var installer = fixture.CreateInstaller(new ScriptedOperations(beforeMove: (_, _, count) =>
        {
            if (count == 5) throw new IOException("Cannot publish the second language.");
        }));

        var error = await Assert.ThrowsAsync<SubtitleGenerationException>(() => installer.InstallAsync(
            fixture.Video, true, true, new(English, Romanian), default));

        Assert.Equal("subtitle_install_failed", error.Code);
        Assert.Equal("original English", File.ReadAllText(fixture.Sidecar(".srt")));
        Assert.Equal("original Romanian", File.ReadAllText(fixture.Sidecar(".ro.srt")));
        Assert.Equal("original ASS", File.ReadAllText(fixture.Sidecar(".ass")));
        Assert.Empty(Directory.GetFiles(fixture.Media, "*.bak"));
        Assert.Empty(Directory.GetFiles(fixture.Media, "*.partial"));
        Assert.Empty(fixture.Journals);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Restart_recovers_interruption_after_each_backup_or_publish_move(int interruptedMove)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original English");
        File.WriteAllText(fixture.Sidecar(".ro.srt"), "original Romanian");
        var installer = fixture.CreateInstaller(new ScriptedOperations(afterMove: (_, _, count) =>
        {
            if (count == interruptedMove) throw new SimulatedProcessTermination();
        }));
        await Assert.ThrowsAsync<SimulatedProcessTermination>(() => installer.InstallAsync(
            fixture.Video, true, true, new(English, Romanian), default));
        Assert.Single(fixture.Journals);

        await fixture.CreateInstaller().RecoverAsync(default);

        Assert.Equal("original English", File.ReadAllText(fixture.Sidecar(".srt")));
        Assert.Equal("original Romanian", File.ReadAllText(fixture.Sidecar(".ro.srt")));
        Assert.Equal("input video", File.ReadAllText(fixture.Video.PhysicalPath));
        Assert.Empty(Directory.GetFiles(fixture.Media, "*.bak"));
        Assert.Empty(Directory.GetFiles(fixture.Media, "*.partial"));
        Assert.Empty(fixture.Journals);
        Assert.False(await fixture.Installer.IsAlreadyGeneratedAsync(fixture.Video, true, true, default));
    }

    [Fact]
    public async Task Restart_finishes_committed_transaction_and_receipt_without_replacing_outputs()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        var installer = fixture.CreateInstaller(new ScriptedOperations(beforeDelete: path =>
        {
            if (path.EndsWith(".json", StringComparison.Ordinal)) throw new SimulatedProcessTermination();
        }));
        await Assert.ThrowsAsync<SimulatedProcessTermination>(() => installer.InstallAsync(
            fixture.Video, true, false, new(English, null), default));
        Assert.Single(fixture.Journals);

        var restarted = fixture.CreateInstaller();
        await restarted.RecoverAsync(default);

        Assert.Contains("Hello there.", File.ReadAllText(fixture.Sidecar(".srt")));
        Assert.Equal("original", File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Media, "*.bak"))));
        Assert.True(await restarted.IsAlreadyGeneratedAsync(fixture.Video, true, false, default));
        Assert.Empty(fixture.Journals);
    }

    [Fact]
    public async Task Restart_refuses_edited_publication_and_only_blocks_its_affected_video()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        var installer = fixture.CreateInstaller(new ScriptedOperations(afterMove: (_, _, count) =>
        {
            if (count == 2) throw new SimulatedProcessTermination();
        }));
        await Assert.ThrowsAsync<SimulatedProcessTermination>(() => installer.InstallAsync(
            fixture.Video, true, false, new(English, null), default));
        File.WriteAllText(fixture.Sidecar(".srt"), "user edited this after interruption");
        var restarted = fixture.CreateInstaller();

        await restarted.RecoverAsync(default);

        Assert.Single(fixture.Journals);
        Assert.Equal("user edited this after interruption", File.ReadAllText(fixture.Sidecar(".srt")));
        Assert.Equal("original", File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Media, "*.bak"))));
        var blocked = await Assert.ThrowsAsync<SubtitleGenerationException>(() => restarted.InstallAsync(
            fixture.Video, true, false, new(English, null), default));
        Assert.Equal("subtitle_recovery_required", blocked.Code);
        var other = fixture.AddVideo("Other.mkv");
        await restarted.InstallAsync(other, true, false, new(English, null), default);
        Assert.Contains("Hello there.", File.ReadAllText(Path.Combine(fixture.Media, "Other.srt")));
    }

    [Fact]
    public async Task An_unresolved_transaction_blocks_the_same_output_basename_after_video_extension_changes()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        var installer = fixture.CreateInstaller(new ScriptedOperations(afterMove: (_, _, count) =>
        {
            if (count == 2) throw new SimulatedProcessTermination();
        }));
        await Assert.ThrowsAsync<SimulatedProcessTermination>(() => installer.InstallAsync(
            fixture.Video, true, false, new(English, null), default));
        var renamedPath = fixture.Sidecar(".mp4");
        File.Move(fixture.Video.PhysicalPath, renamedPath);
        var renamed = fixture.Video with { LogicalPath = "/Movie.mp4", PhysicalPath = renamedPath };

        var error = await Assert.ThrowsAsync<SubtitleGenerationException>(() => fixture.CreateInstaller().InstallAsync(
            renamed, true, false, new(English, null), default));

        Assert.Equal("subtitle_recovery_required", error.Code);
        Assert.Single(fixture.Journals);
        Assert.Equal("original", File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Media, "*.bak"))));
    }

    [Fact]
    public async Task Failed_rollback_is_retried_after_restart()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        var installer = fixture.CreateInstaller(new ScriptedOperations(beforeMove: (_, _, count) =>
        {
            if (count is 2 or 3) throw new IOException("Publish and immediate restore both fail.");
        }));
        var error = await Assert.ThrowsAsync<SubtitleGenerationException>(() => installer.InstallAsync(
            fixture.Video, true, false, new(English, null), default));
        Assert.Equal("subtitle_recovery_required", error.Code);
        Assert.False(File.Exists(fixture.Sidecar(".srt")));

        await fixture.CreateInstaller().RecoverAsync(default);

        Assert.Equal("original", File.ReadAllText(fixture.Sidecar(".srt")));
        Assert.Empty(fixture.Journals);
    }

    [Fact]
    public async Task Malformed_journal_is_isolated_and_does_not_crash_recovery()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        var installer = fixture.CreateInstaller(new ScriptedOperations(afterMove: (_, _, _) => throw new SimulatedProcessTermination()));
        await Assert.ThrowsAsync<SimulatedProcessTermination>(() => installer.InstallAsync(
            fixture.Video, true, false, new(English, null), default));
        File.WriteAllText(Assert.Single(fixture.Journals), "{\"version\":1}");

        await fixture.CreateInstaller().RecoverAsync(default);

        Assert.Single(fixture.Journals);
        Assert.Equal("original", File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Media, "*.bak"))));
        var other = fixture.AddVideo("Other.mkv");
        await fixture.CreateInstaller().InstallAsync(other, true, false, new(English, null), default);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("originals")]
    [InlineData("outputs")]
    public async Task Null_journal_fields_do_not_crash_recovery(string field)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        var installer = fixture.CreateInstaller(new ScriptedOperations(afterMove: (_, _, _) => throw new SimulatedProcessTermination()));
        await Assert.ThrowsAsync<SimulatedProcessTermination>(() => installer.InstallAsync(
            fixture.Video, true, false, new(English, null), default));
        var path = Assert.Single(fixture.Journals);
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document[field] = null;
        File.WriteAllText(path, document.ToJsonString());

        await fixture.CreateInstaller().RecoverAsync(default);

        Assert.Single(fixture.Journals);
        Assert.Equal("original", File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Media, "*.bak"))));
    }

    [Fact]
    public async Task Cancellation_after_first_backup_restores_originals()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        var installer = fixture.CreateInstaller(new ScriptedOperations(afterMove: (_, _, count) =>
        {
            if (count == 1) cancellation.Cancel();
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(
            fixture.Video, true, true, new(English, Romanian), cancellation.Token));
        Assert.Equal("original", File.ReadAllText(fixture.Sidecar(".srt")));
        Assert.Empty(fixture.Journals);
        Assert.Empty(Directory.GetFiles(fixture.Media, "*.partial"));
    }

    [Fact]
    public async Task Does_not_overwrite_a_backup_created_between_planning_and_move()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        var installer = fixture.CreateInstaller(new ScriptedOperations(beforeMove: (_, destination, count) =>
        {
            if (count == 1) File.WriteAllText(destination, "someone else's file");
        }));
        await Assert.ThrowsAsync<SubtitleGenerationException>(() => installer.InstallAsync(
            fixture.Video, true, false, new(English, null), default));
        Assert.Equal("original", File.ReadAllText(fixture.Sidecar(".srt")));
        Assert.Equal("someone else's file", File.ReadAllText(Assert.Single(Directory.GetFiles(fixture.Media, "*.bak"))));
    }

    [Theory]
    [InlineData(".srt")]
    [InlineData(".en.forced.ass")]
    public async Task Rejects_symbolic_link_sidecars_even_if_target_is_inside_source(string suffix)
    {
        using var fixture = new Fixture();
        var target = Path.Combine(fixture.Media, "unrelated.txt");
        File.WriteAllText(target, "do not modify");
        try { File.CreateSymbolicLink(fixture.Sidecar(suffix), target); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return; }
        await Assert.ThrowsAsync<SubtitleGenerationException>(() => fixture.Installer.InstallAsync(
            fixture.Video, true, false, new(English, null), default));
        Assert.Equal("do not modify", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(fixture.Sidecar(suffix)).LinkTarget);
        Assert.Empty(Directory.GetFiles(fixture.Media, "*.bak"));
    }

    [Fact]
    public async Task Rejects_a_symbolic_link_video_even_when_resolution_points_inside_source()
    {
        using var fixture = new Fixture();
        var link = Path.Combine(fixture.Media, "Linked.mkv");
        try { File.CreateSymbolicLink(link, fixture.Video.PhysicalPath); }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows()) { return; }
        var linked = fixture.Video with { LogicalPath = "/Linked.mkv" };
        await Assert.ThrowsAsync<SubtitleGenerationException>(() => fixture.Installer.InstallAsync(
            linked, true, false, new(English, null), default));
        Assert.False(File.Exists(fixture.Sidecar(".srt")));
    }

    private sealed class SimulatedProcessTermination : Exception;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Partial_stage_write_failure_cleans_owned_partial_and_preserves_originals(bool cancelled)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Sidecar(".srt"), "original");
        var installer = fixture.CreateInstaller(new PartialWriteOperations(cancelled));

        if (cancelled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.InstallAsync(
                fixture.Video, true, false, new(English, null), default));
        else
        {
            var error = await Assert.ThrowsAsync<SubtitleGenerationException>(() => installer.InstallAsync(
                fixture.Video, true, false, new(English, null), default));
            Assert.Equal("subtitle_install_failed", error.Code);
        }

        Assert.Equal("original", File.ReadAllText(fixture.Sidecar(".srt")));
        Assert.Empty(Directory.GetFiles(fixture.Media, "*.partial"));
        Assert.Empty(fixture.Journals);
    }

    private sealed class PartialWriteOperations(bool cancelled) : SubtitleInstallerFileOperations
    {
        protected override async ValueTask WriteContentsAsync(FileStream stream, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken)
        {
            await stream.WriteAsync(contents[..12], cancellationToken);
            if (cancelled) throw new OperationCanceledException();
            throw new IOException("Disk failed after part of the stage was written.");
        }
    }

    private sealed class ScriptedOperations(
        Action<string, string, int>? beforeMove = null,
        Action<string, string, int>? afterMove = null,
        Action<string>? beforeDelete = null) : SubtitleInstallerFileOperations
    {
        private int _moveCount;
        public override void Move(string source, string destination)
        {
            var count = ++_moveCount;
            beforeMove?.Invoke(source, destination, count);
            base.Move(source, destination);
            afterMove?.Invoke(source, destination, count);
        }
        public override void Delete(string path)
        {
            beforeDelete?.Invoke(path);
            base.Delete(path);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary = new();
        private readonly DirectoryMutationLock _mutationLock = new();
        public DirectoryMutationLock MutationLock => _mutationLock;
        public Fixture(string name = "Movie.mkv")
        {
            Media = _temporary.CreateDirectory("media");
            Data = _temporary.CreateDirectory("data");
            var physical = Path.Combine(Media, name);
            File.WriteAllText(physical, "input video");
            var info = new FileInfo(physical);
            Video = new("media", "/" + name, physical, "/", info.Length, info.LastWriteTimeUtc.Ticks);
            Catalog = new(new("media", "Media", Media, false, true, false));
            Installer = CreateInstaller();
        }
        public string Media { get; }
        public string Data { get; }
        public SubtitleVideo Video { get; }
        public MutableSourceCatalog Catalog { get; }
        public SubtitleInstaller Installer { get; }
        public string[] Journals => Directory.GetFiles(Path.Combine(Data, "subtitle-generation", "transactions"), "*.json");
        public SubtitleVideo AddVideo(string name)
        {
            var physical = Path.Combine(Media, name);
            File.WriteAllText(physical, "input video");
            var info = new FileInfo(physical);
            return new("media", "/" + name, physical, "/", info.Length, info.LastWriteTimeUtc.Ticks);
        }
        public string Sidecar(string suffix) => Path.Combine(Media, Path.GetFileNameWithoutExtension(Video.PhysicalPath) + suffix);
        public SubtitleInstaller CreateInstaller(SubtitleInstallerFileOperations? operations = null, long minimumFreeBytes = 0) => new(
            AuthenticationDataPaths.ForRoot(Data), new PathSecurityService(Catalog), _mutationLock,
            Options.Create(new SubtitleGenerationOptions { MinimumFreeBytes = minimumFreeBytes }),
            NullLogger<SubtitleInstaller>.Instance, operations);
        public void Dispose() => _temporary.Dispose();
    }

    private sealed class MutableSourceCatalog(SourceDefinition source) : ISourceCatalog
    {
        public SourceDefinition Source { get; set; } = source;
        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceDefinition>>([Source]);
        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<SourceDefinition> GetRequiredAsync(string sourceId, CancellationToken cancellationToken) => ValueTask.FromResult(Source);
    }
}
