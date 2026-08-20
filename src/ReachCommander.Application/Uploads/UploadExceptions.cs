namespace ReachCommander.Application.Uploads;

public abstract class UploadException(string code, string detail) : Exception(detail)
{
    public string Code { get; } = code;

    public string Detail { get; } = detail;
}

public sealed class UploadEmptyException()
    : UploadException("upload_empty", "Choose at least one file to upload.");

public sealed class UploadNameInvalidException(string fileName)
    : UploadException("upload_name_invalid", "One or more filenames are not valid for upload.")
{
    public string FileName { get; } = fileName;
}

public sealed class UploadNameConflictException(IEnumerable<string> fileNames)
    : UploadException("upload_name_conflict", "One or more filenames already exist or are duplicated in the batch.")
{
    public IReadOnlyList<string> FileNames { get; } = Array.AsReadOnly(fileNames.ToArray());
}

public sealed class UploadFileTooLargeException(string fileName, long maxBytes)
    : UploadException("upload_file_too_large", $"File '{fileName}' exceeds the configured {maxBytes}-byte limit.")
{
    public string FileName { get; } = fileName;

    public long MaxBytes { get; } = maxBytes;
}

public sealed class UploadBatchTooLargeException(long maxBytes)
    : UploadException("upload_batch_too_large", $"The upload batch exceeds the configured {maxBytes}-byte limit.")
{
    public long MaxBytes { get; } = maxBytes;
}

public sealed class UploadTooManyFilesException(int maxFiles)
    : UploadException("upload_too_many_files", $"The upload batch exceeds the configured {maxFiles}-file limit.")
{
    public int MaxFiles { get; } = maxFiles;
}

public sealed class UploadSourceReadOnlyException(string sourceId)
    : UploadException("source_read_only", $"Source '{sourceId}' does not allow file uploads.")
{
    public string SourceId { get; } = sourceId;
}

public sealed class UploadStorageUnavailableException()
    : UploadException("upload_storage_unavailable", "The destination storage is not available for this upload.");

public sealed class UploadUnsupportedMediaTypeException()
    : UploadException("upload_unsupported_media_type", "The request must use multipart/form-data.");

public sealed class UploadMalformedRequestException()
    : UploadException("upload_malformed", "The multipart upload request is malformed.");

public sealed class UploadCancelledException()
    : UploadException("upload_cancelled", "The upload was cancelled.");

public sealed class UploadCleanupRequiredException(IEnumerable<string> fileNames)
    : UploadException("upload_cleanup_required", "The upload failed and some temporary entries require administrator cleanup.")
{
    public IReadOnlyList<string> FileNames { get; } = Array.AsReadOnly(fileNames.ToArray());
}
