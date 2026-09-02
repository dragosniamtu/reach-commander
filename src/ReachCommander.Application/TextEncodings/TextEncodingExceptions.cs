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
}
