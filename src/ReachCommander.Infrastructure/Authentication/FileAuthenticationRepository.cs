using System.Text.Json;
using ReachCommander.Application.Authentication;

namespace ReachCommander.Infrastructure.Authentication;

internal interface IAtomicAuthenticationFileWriter
{
    Task CreateAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);

    Task ReplaceAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);
}

internal sealed class FileAuthenticationRepository
{
    private const int DocumentVersion = 1;
    private const int MaximumDocumentBytes = 65_536;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly AuthenticationDataPaths _paths;
    private readonly IAtomicAuthenticationFileWriter _writer;
    private readonly SemaphoreSlim _processGate = new(1, 1);

    public FileAuthenticationRepository(AuthenticationDataPaths paths)
        : this(paths, new AtomicAuthenticationFileWriter())
    {
    }

    internal FileAuthenticationRepository(
        AuthenticationDataPaths paths,
        IAtomicAuthenticationFileWriter writer)
    {
        _paths = paths;
        _writer = writer;
    }

    public ValueTask<AdministratorAccountDocument?> ReadAccountAsync(
        CancellationToken cancellationToken) =>
        ReadDocumentAsync<AdministratorAccountDocument>(
            _paths.AccountPath,
            ValidateAccount,
            cancellationToken);

    public ValueTask<BootstrapDocument?> ReadBootstrapAsync(
        CancellationToken cancellationToken) =>
        ReadDocumentAsync<BootstrapDocument>(
            _paths.BootstrapPath,
            ValidateBootstrap,
            cancellationToken);

    public Task CreateAccountAsync(
        AdministratorAccountDocument document,
        CancellationToken cancellationToken)
    {
        ValidateAccount(document);
        return MutateAsync(
            () => _writer.CreateAsync(
                _paths.AccountPath,
                Serialize(document),
                cancellationToken),
            translateExistingAccount: true,
            cancellationToken);
    }

    public Task ReplaceAccountAsync(
        AdministratorAccountDocument document,
        CancellationToken cancellationToken)
    {
        ValidateAccount(document);
        return MutateAsync(
            () => _writer.ReplaceAsync(
                _paths.AccountPath,
                Serialize(document),
                cancellationToken),
            translateExistingAccount: false,
            cancellationToken);
    }

    public Task ReplaceBootstrapAsync(
        BootstrapDocument document,
        CancellationToken cancellationToken)
    {
        ValidateBootstrap(document);
        return MutateAsync(
            () => _writer.ReplaceAsync(
                _paths.BootstrapPath,
                Serialize(document),
                cancellationToken),
            translateExistingAccount: false,
            cancellationToken);
    }

    public Task DeleteBootstrapAsync(CancellationToken cancellationToken) =>
        MutateAsync(
            () =>
            {
                File.Delete(_paths.BootstrapPath);
                return Task.CompletedTask;
            },
            translateExistingAccount: false,
            cancellationToken);

    private async ValueTask<TDocument?> ReadDocumentAsync<TDocument>(
        string path,
        Action<TDocument> validate,
        CancellationToken cancellationToken)
        where TDocument : class
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }

            if (file.Length > MaximumDocumentBytes)
            {
                throw InvalidDocument();
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<TDocument>(
                stream,
                SerializerOptions,
                cancellationToken);
            if (document is null)
            {
                throw InvalidDocument();
            }

            validate(document);
            return document;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (AuthenticationStateUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            throw new AuthenticationStateUnavailableException(
                "Authentication state could not be read safely.",
                exception);
        }
    }

    private async Task MutateAsync(
        Func<Task> mutation,
        bool translateExistingAccount,
        CancellationToken cancellationToken)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            _paths.EnsureDirectories();
            await using var fileLock = await AcquireFileLockAsync(cancellationToken);
            try
            {
                await mutation();
            }
            catch (IOException) when (
                translateExistingAccount && File.Exists(_paths.AccountPath))
            {
                throw new AdministratorAlreadyExistsException();
            }
        }
        catch (AuthenticationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new AuthenticationStateUnavailableException(
                "Authentication state could not be updated safely.",
                exception);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        const int maximumAttempts = 100;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    _paths.LockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
                try
                {
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(
                            _paths.LockPath,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }

                    return stream;
                }
                catch
                {
                    await stream.DisposeAsync();
                    throw;
                }
            }
            catch (IOException) when (attempt < maximumAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
            }
        }

        throw new AuthenticationStateUnavailableException(
            "Authentication state is busy and could not be locked.");
    }

    private static ReadOnlyMemory<byte> Serialize<TDocument>(TDocument document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
        if (bytes.Length > MaximumDocumentBytes)
        {
            throw InvalidDocument();
        }

        return bytes;
    }

    private static void ValidateAccount(AdministratorAccountDocument document)
    {
        if (document.Version != DocumentVersion ||
            string.IsNullOrWhiteSpace(document.Username) ||
            string.IsNullOrWhiteSpace(document.NormalizedUsername) ||
            string.IsNullOrWhiteSpace(document.PasswordHash) ||
            string.IsNullOrWhiteSpace(document.SecurityStamp) ||
            document.Username.Length > 64 ||
            document.NormalizedUsername.Length > 64 ||
            document.PasswordHash.Length > 4096 ||
            document.SecurityStamp.Length > 256)
        {
            throw InvalidDocument();
        }
    }

    private static void ValidateBootstrap(BootstrapDocument document)
    {
        if (document.Version != DocumentVersion || string.IsNullOrWhiteSpace(document.Verifier))
        {
            throw InvalidDocument();
        }

        try
        {
            if (Convert.FromBase64String(document.Verifier).Length != 32)
            {
                throw InvalidDocument();
            }
        }
        catch (FormatException exception)
        {
            throw new AuthenticationStateUnavailableException(
                "Authentication state contains an invalid bootstrap verifier.",
                exception);
        }
    }

    private static AuthenticationStateUnavailableException InvalidDocument() =>
        new("Authentication state is malformed or uses an unsupported version.");
}

internal sealed class AtomicAuthenticationFileWriter : IAtomicAuthenticationFileWriter
{
    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public Task CreateAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken) =>
        WriteAsync(destinationPath, content, overwrite: false, cancellationToken);

    public Task ReplaceAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken) =>
        WriteAsync(destinationPath, content, overwrite: true, cancellationToken);

    private static async Task WriteAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destinationPath) ??
            throw new IOException("Authentication destination has no parent directory.");
        var fileName = Path.GetFileName(destinationPath);
        var temporaryPath = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, OwnerFileMode);
            }

            File.Move(temporaryPath, destinationPath, overwrite);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
