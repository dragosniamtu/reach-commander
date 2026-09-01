using ReachCommander.Infrastructure.MediaPreviews;

namespace ReachCommander.UnitTests.MediaPreviews;

public sealed class MediaPreviewOptionsValidatorTests
{
    private readonly MediaPreviewOptionsValidator _validator = new();

    [Fact]
    public void Defaults_use_the_approved_low_CPU_profile()
    {
        var options = new MediaPreviewOptions();

        Assert.Equal(2, options.MaximumTranscodeThreads);
        Assert.Equal("ultrafast", options.TranscodePreset);
        Assert.True(_validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void Rejects_thread_limits_outside_the_approved_range(int value)
    {
        var result = _validator.Validate(
            null,
            new MediaPreviewOptions { MaximumTranscodeThreads = value });

        Assert.False(result.Succeeded);
        Assert.Contains("MaximumTranscodeThreads", result.FailureMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("medium")]
    [InlineData("ultrafast\n-injected")]
    public void Rejects_presets_outside_the_approved_allowlist(string value)
    {
        var result = _validator.Validate(
            null,
            new MediaPreviewOptions { TranscodePreset = value });

        Assert.False(result.Succeeded);
        Assert.Contains("TranscodePreset", result.FailureMessage);
    }
}
