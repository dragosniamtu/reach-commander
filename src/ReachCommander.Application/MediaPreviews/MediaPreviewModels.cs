namespace ReachCommander.Application.MediaPreviews;

public sealed record SubtitleCue(
    int Index,
    long StartMilliseconds,
    long EndMilliseconds,
    string Text);
