using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Sources;
using ReachCommander.Infrastructure.Configuration;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.Sources;

public sealed class JsonSourceCatalogTests
{
    [Fact]
    public async Task GetDefinitionsAsync_loads_enabled_sources_and_ignores_disabled_sources()
    {
        using var temporary = new TemporaryDirectory();
        var availableRoot = temporary.CreateDirectory("downloads");
        var catalog = CreateCatalog(temporary, $$"""
            {
              "sources": [
                { "id": "downloads", "name": "Downloads", "path": {{Json(availableRoot)}}, "enabled": true, "readOnly": false, "defaultLeft": true },
                { "id": "archive", "name": "Archive", "path": {{Json(temporary.Path)}}, "enabled": false, "readOnly": true }
              ]
            }
            """);

        var definitions = await catalog.GetDefinitionsAsync(CancellationToken.None);

        var definition = Assert.Single(definitions);
        Assert.Equal("downloads", definition.Id);
        Assert.Equal("Downloads", definition.Name);
        Assert.Equal(availableRoot, definition.RootPath);
        Assert.True(definition.DefaultLeft);
        Assert.False(definition.DefaultRight);
    }

    [Fact]
    public async Task GetSnapshotsAsync_keeps_an_unavailable_source_visible()
    {
        using var temporary = new TemporaryDirectory();
        var missingRoot = System.IO.Path.Combine(temporary.Path, "missing");
        var catalog = CreateCatalog(temporary, $$"""
            {
              "sources": [
                { "id": "usb", "name": "USB", "path": {{Json(missingRoot)}}, "enabled": true, "readOnly": true, "defaultRight": true }
              ]
            }
            """);

        var snapshots = await catalog.GetSnapshotsAsync(CancellationToken.None);

        var snapshot = Assert.Single(snapshots);
        Assert.Equal("usb", snapshot.Id);
        Assert.False(snapshot.IsAvailable);
        Assert.True(snapshot.IsReadOnly);
        Assert.Null(snapshot.TotalBytes);
        Assert.Null(snapshot.UsedBytes);
        Assert.Null(snapshot.FreeBytes);
    }

    [Theory]
    [InlineData("Downloads", "invalid source id")]
    [InlineData("media!", "invalid source id")]
    public async Task GetDefinitionsAsync_rejects_invalid_ids(string sourceId, string expectedMessage)
    {
        using var temporary = new TemporaryDirectory();
        var catalog = CreateCatalog(temporary, $$"""
            { "sources": [ { "id": {{Json(sourceId)}}, "name": "Media", "path": {{Json(temporary.Path)}}, "enabled": true } ] }
            """);

        var error = await Assert.ThrowsAsync<SourceConfigurationException>(
            () => catalog.GetDefinitionsAsync(CancellationToken.None).AsTask());

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDefinitionsAsync_rejects_duplicate_ids_case_insensitively()
    {
        using var temporary = new TemporaryDirectory();
        var catalog = CreateCatalog(temporary, $$"""
            {
              "sources": [
                { "id": "media", "name": "Media", "path": {{Json(temporary.Path)}}, "enabled": true },
                { "id": "MEDIA", "name": "Backup", "path": {{Json(temporary.Path)}}, "enabled": true }
              ]
            }
            """);

        var error = await Assert.ThrowsAsync<SourceConfigurationException>(
            () => catalog.GetDefinitionsAsync(CancellationToken.None).AsTask());

        Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDefinitionsAsync_rejects_relative_roots()
    {
        using var temporary = new TemporaryDirectory();
        var catalog = CreateCatalog(temporary, """
            { "sources": [ { "id": "media", "name": "Media", "path": "relative/media", "enabled": true } ] }
            """);

        var error = await Assert.ThrowsAsync<SourceConfigurationException>(
            () => catalog.GetDefinitionsAsync(CancellationToken.None).AsTask());

        Assert.Contains("absolute", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDefinitionsAsync_rejects_an_empty_display_name()
    {
        using var temporary = new TemporaryDirectory();
        var catalog = CreateCatalog(temporary, $$"""
            { "sources": [ { "id": "media", "name": "   ", "path": {{Json(temporary.Path)}}, "enabled": true } ] }
            """);

        var error = await Assert.ThrowsAsync<SourceConfigurationException>(
            () => catalog.GetDefinitionsAsync(CancellationToken.None).AsTask());

        Assert.Contains("name", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("defaultLeft")]
    [InlineData("defaultRight")]
    public async Task GetDefinitionsAsync_rejects_multiple_defaults(string defaultProperty)
    {
        using var temporary = new TemporaryDirectory();
        var catalog = CreateCatalog(temporary, $$"""
            {
              "sources": [
                { "id": "one", "name": "One", "path": {{Json(temporary.Path)}}, "enabled": true, {{Json(defaultProperty)}}: true },
                { "id": "two", "name": "Two", "path": {{Json(temporary.Path)}}, "enabled": true, {{Json(defaultProperty)}}: true }
              ]
            }
            """);

        var error = await Assert.ThrowsAsync<SourceConfigurationException>(
            () => catalog.GetDefinitionsAsync(CancellationToken.None).AsTask());

        Assert.Contains("default", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetDefinitionsAsync_rejects_configuration_without_enabled_sources()
    {
        using var temporary = new TemporaryDirectory();
        var catalog = CreateCatalog(temporary, $$"""
            { "sources": [ { "id": "off", "name": "Off", "path": {{Json(temporary.Path)}}, "enabled": false } ] }
            """);

        var error = await Assert.ThrowsAsync<SourceConfigurationException>(
            () => catalog.GetDefinitionsAsync(CancellationToken.None).AsTask());

        Assert.Contains("enabled", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRequiredAsync_throws_for_unknown_source()
    {
        using var temporary = new TemporaryDirectory();
        var catalog = CreateCatalog(temporary, $$"""
            { "sources": [ { "id": "media", "name": "Media", "path": {{Json(temporary.Path)}}, "enabled": true } ] }
            """);

        await Assert.ThrowsAsync<SourceNotFoundException>(
            () => catalog.GetRequiredAsync("unknown", CancellationToken.None).AsTask());
    }

    private static JsonSourceCatalog CreateCatalog(TemporaryDirectory temporary, string json)
    {
        var path = temporary.Write("sources.json", json);
        return new JsonSourceCatalog(
            Options.Create(new ReachCommanderOptions { SourcesPath = path }),
            NullLogger<JsonSourceCatalog>.Instance);
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
