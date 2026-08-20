namespace ReachCommander.ArchiveWorker;

internal sealed class ConcatenatedFileReadStream : Stream
{
    private readonly FileStream[] streams;
    private readonly long[] offsets;
    private readonly long length;
    private long position;
    private bool disposed;

    public ConcatenatedFileReadStream(IReadOnlyList<FileInfo> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException("At least one file is required.", nameof(files));
        }

        streams = new FileStream[files.Count];
        offsets = new long[files.Count];
        try
        {
            long nextOffset = 0;
            for (var index = 0; index < files.Count; index++)
            {
                offsets[index] = nextOffset;
                streams[index] = new FileStream(
                    files[index].FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.RandomAccess);
                nextOffset = checked(nextOffset + streams[index].Length);
            }

            length = nextOffset;
        }
        catch
        {
            foreach (var stream in streams)
            {
                stream?.Dispose();
            }

            throw;
        }
    }

    public override bool CanRead => !disposed;

    public override bool CanSeek => !disposed;

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return position;
        }
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        var totalRead = 0;
        while (!buffer.IsEmpty && position < length)
        {
            var stream = CurrentStream();
            var localPosition = position - offsets[stream.Index];
            if (stream.Value.Position != localPosition)
            {
                stream.Value.Position = localPosition;
            }

            var available = stream.Value.Length - localPosition;
            var requested = (int)Math.Min(buffer.Length, available);
            var read = stream.Value.Read(buffer[..requested]);
            if (read == 0)
            {
                throw new EndOfStreamException("An archive volume changed while it was being read.");
            }

            position += read;
            totalRead += read;
            buffer = buffer[read..];
        }

        return totalRead;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var totalRead = 0;
        while (!buffer.IsEmpty && position < length)
        {
            var stream = CurrentStream();
            var localPosition = position - offsets[stream.Index];
            if (stream.Value.Position != localPosition)
            {
                stream.Value.Position = localPosition;
            }

            var available = stream.Value.Length - localPosition;
            var requested = (int)Math.Min(buffer.Length, available);
            var read = await stream.Value.ReadAsync(
                buffer[..requested],
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("An archive volume changed while it was being read.");
            }

            position += read;
            totalRead += read;
            buffer = buffer[read..];
        }

        return totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        var next = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (next < 0 || next > length)
        {
            throw new IOException("Attempted to seek outside the archive volume set.");
        }

        position = next;
        return position;
    }

    public override void Flush()
    {
        ThrowIfDisposed();
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!disposed && disposing)
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }
        }

        disposed = true;
        base.Dispose(disposing);
    }

    private (int Index, FileStream Value) CurrentStream()
    {
        for (var index = streams.Length - 1; index >= 0; index--)
        {
            if (position >= offsets[index] && position < offsets[index] + streams[index].Length)
            {
                return (index, streams[index]);
            }
        }

        throw new EndOfStreamException("The archive volume set ended unexpectedly.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
