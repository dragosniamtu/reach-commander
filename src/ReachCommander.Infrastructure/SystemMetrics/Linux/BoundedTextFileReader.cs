namespace ReachCommander.Infrastructure.SystemMetrics.Linux;

internal sealed class BoundedTextFileReader
{
    public const int MaximumFileCharacters = 1_048_576;

    public async ValueTask<string> ReadRequiredAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaximumFileCharacters + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);

        if (count > MaximumFileCharacters)
        {
            throw new InvalidDataException("Hardware metrics input exceeds its read limit.");
        }

        return new string(buffer, 0, count);
    }
}
