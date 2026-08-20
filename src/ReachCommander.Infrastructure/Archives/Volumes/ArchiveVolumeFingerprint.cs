using System.Security.Cryptography;
using System.Text;

namespace ReachCommander.Infrastructure.Archives.Volumes;

internal sealed record ArchiveVolumeFingerprint(string Value)
{
    public static ArchiveVolumeFingerprint Create(
        string sourceId,
        string primaryLogicalPath,
        IReadOnlyList<ResolvedArchivePart> parts)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(sourceId);
            writer.Write(primaryLogicalPath);
            writer.Write(parts.Count);
            foreach (var part in parts)
            {
                writer.Write(part.LogicalPath);
                writer.Write(part.Length);
                writer.Write(part.LastWriteTimeUtc.UtcTicks);
            }
        }

        return new ArchiveVolumeFingerprint(
            Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))));
    }
}
