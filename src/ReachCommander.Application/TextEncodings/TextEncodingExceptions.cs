namespace ReachCommander.Application.TextEncodings;

public sealed class TextEncodingException(
    string code,
    string publicDetail,
    Exception? innerException = null) : Exception(publicDetail, innerException)
{
    public string Code { get; } = code;

    public string PublicDetail { get; } = publicDetail;

    public static TextEncodingException BinaryContent() => new(
        "text_binary_content",
        "The selected file appears to contain binary content.");

    public static TextEncodingException DecodeFailed(Exception? innerException = null) => new(
        "text_decode_failed",
        "The file cannot be decoded using the selected source encoding.",
        innerException);

    public static TextEncodingException OutputUnrepresentable(Exception? innerException = null) => new(
        "text_output_unrepresentable",
        "The selected output encoding cannot represent every character in the file.",
        innerException);

    public static TextEncodingException InvalidSourceEncoding() => new(
        "text_encoding_invalid_source",
        "The selected source encoding is not supported.");

    public static TextEncodingException InvalidOutputEncoding() => new(
        "text_encoding_invalid_output",
        "The selected output encoding is not supported.");

    public static TextEncodingException InvalidRequest() => new(
        "text_encoding_invalid_request",
        "Select between 1 and 100 supported text files from one directory.");

    public static TextEncodingException PlanNotFound() => new(
        "text_encoding_plan_not_found",
        "The encoding preview was not found. Preview the files again.");

    public static TextEncodingException PlanExpired() => new(
        "text_encoding_plan_expired",
        "The encoding preview expired. Preview the files again.");

    public static TextEncodingException FileTooLarge() => new(
        "text_file_too_large",
        "The file exceeds the 32 MiB text conversion limit.");

    public static TextEncodingException FileNotRegular() => new(
        "text_file_not_regular",
        "The selected entry is not a regular file.");

    public static TextEncodingException SymbolicLinkRejected() => new(
        "text_symbolic_link_rejected",
        "Symbolic links and reparse points cannot be converted.");

    public static TextEncodingException UnsupportedExtension() => new(
        "unsupported_text_extension",
        "The selected file extension is not supported by the encoding tool.");

    public static TextEncodingException OperationNotFound() => new(
        "text_encoding_operation_not_found",
        "The encoding operation was not found.");

    public static TextEncodingException OperationExpired() => new(
        "text_encoding_operation_expired",
        "The encoding operation has expired.");

    public static TextEncodingException CapacityReached() => new(
        "text_encoding_capacity_reached",
        "Another text encoding operation is already running.");
}
