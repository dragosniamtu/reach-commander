using System.Runtime.CompilerServices;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using ReachCommander.Application.Uploads;
using ReachCommander.Infrastructure.Uploads;

namespace ReachCommander.Api.Uploads;

public sealed class MultipartUploadReader
{
    private const int MaximumBoundaryLength = 128;
    private const int MaximumHeaderCount = 32;
    private const int MaximumHeaderLength = 16 * 1024;

    public IAsyncEnumerable<UploadFilePart> ReadAsync(
        HttpRequest request,
        UploadOptions options,
        CancellationToken cancellationToken)
    {
        var boundary = GetBoundary(request.ContentType);
        return ReadCoreAsync(request, boundary, options, cancellationToken);
    }

    private static async IAsyncEnumerable<UploadFilePart> ReadCoreAsync(
        HttpRequest request,
        string boundary,
        UploadOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var terminalOnly = Encoding.ASCII.GetBytes($"--{boundary}--\r\n");
        var dotNetEmptyForm = Encoding.ASCII.GetBytes(
            $"--{boundary}\r\n\r\n--{boundary}--\r\n");
        var prefix = await ReadPrefixAsync(
            request.Body,
            Math.Max(terminalOnly.Length, dotNetEmptyForm.Length) + 1,
            cancellationToken).ConfigureAwait(false);
        if (IsExact(prefix, terminalOnly) || IsExact(prefix, dotNetEmptyForm))
        {
            yield break;
        }

        var reader = new MultipartReader(
            boundary,
            new PrefixReplayStream(prefix.Buffer, prefix.Count, request.Body))
        {
            HeadersCountLimit = MaximumHeaderCount,
            HeadersLengthLimit = MaximumHeaderLength,
            BodyLengthLimit = options.MaxFileBytes,
        };

        while (await ReadNextSectionAsync(reader, cancellationToken).ConfigureAwait(false) is { } section)
        {
            var headers = section.Headers ?? throw new UploadMalformedRequestException();
            if (!headers.TryGetValue(HeaderNames.ContentDisposition, out var rawDisposition) ||
                !ContentDispositionHeaderValue.TryParse(
                    new StringSegment(rawDisposition.ToString()),
                    out var disposition) ||
                disposition is null ||
                !disposition.DispositionType.Equals("form-data") ||
                !string.Equals(
                    HeaderUtilities.RemoveQuotes(disposition.Name).Value,
                    "files",
                    StringComparison.Ordinal) ||
                !HasFileName(disposition))
            {
                throw new UploadMalformedRequestException();
            }

            var fileName = GetFileName(disposition);
            var declaredLength = GetDeclaredLength(headers);
            yield return new UploadFilePart(
                fileName,
                new UploadSectionStream(section.Body, fileName, options.MaxFileBytes),
                declaredLength);
        }
    }

    private static async ValueTask<PrefixBuffer> ReadPrefixAsync(
        Stream body,
        int capacity,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[capacity];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await body
                .ReadAsync(buffer.AsMemory(count), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        return new PrefixBuffer(buffer, count);
    }

    private static bool IsExact(PrefixBuffer prefix, byte[] expected) =>
        prefix.Count == expected.Length &&
        prefix.Buffer.AsSpan(0, prefix.Count).SequenceEqual(expected);

    private static long? GetDeclaredLength(IReadOnlyDictionary<string, StringValues> headers)
    {
        if (!headers.TryGetValue(HeaderNames.ContentLength, out var rawLength))
        {
            return null;
        }

        if (!long.TryParse(
                rawLength.ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var declaredLength) ||
            declaredLength < 0)
        {
            throw new UploadMalformedRequestException();
        }

        return declaredLength;
    }

    private static async ValueTask<MultipartSection?> ReadNextSectionAsync(
        MultipartReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            throw new UploadMalformedRequestException();
        }
    }

    private static string GetBoundary(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType) ||
            !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new UploadUnsupportedMediaTypeException();
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > MaximumBoundaryLength)
        {
            throw new UploadMalformedRequestException();
        }

        return boundary;
    }

    private static bool HasFileName(ContentDispositionHeaderValue disposition) =>
        !StringSegment.IsNullOrEmpty(disposition.FileNameStar) ||
        !StringSegment.IsNullOrEmpty(disposition.FileName);

    private static string GetFileName(ContentDispositionHeaderValue disposition)
    {
        var value = !StringSegment.IsNullOrEmpty(disposition.FileNameStar)
            ? disposition.FileNameStar
            : disposition.FileName;
        return HeaderUtilities.RemoveQuotes(value).Value ?? string.Empty;
    }

    private sealed class UploadSectionStream(
        Stream inner,
        string fileName,
        long maxFileBytes) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            try
            {
                return inner.Read(buffer, offset, count);
            }
            catch (InvalidDataException)
            {
                throw new UploadFileTooLargeException(fileName, maxFileBytes);
            }
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                throw new UploadFileTooLargeException(fileName, maxFileBytes);
            }
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return ReadArrayAsync(buffer, offset, count, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
        }

        private async Task<int> ReadArrayAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            try
            {
                return await inner
                    .ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                throw new UploadFileTooLargeException(fileName, maxFileBytes);
            }
        }
    }

    private sealed class PrefixReplayStream(
        byte[] prefix,
        int prefixLength,
        Stream inner) : Stream
    {
        private int _prefixPosition;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            if (_prefixPosition < prefixLength)
            {
                var count = Math.Min(buffer.Length, prefixLength - _prefixPosition);
                prefix.AsSpan(_prefixPosition, count).CopyTo(buffer);
                _prefixPosition += count;
                return count;
            }

            return inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_prefixPosition < prefixLength)
            {
                var count = Math.Min(buffer.Length, prefixLength - _prefixPosition);
                prefix.AsMemory(_prefixPosition, count).CopyTo(buffer);
                _prefixPosition += count;
                return ValueTask.FromResult(count);
            }

            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadArrayAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
        }

        private async Task<int> ReadArrayAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    private sealed record PrefixBuffer(byte[] Buffer, int Count);
}
