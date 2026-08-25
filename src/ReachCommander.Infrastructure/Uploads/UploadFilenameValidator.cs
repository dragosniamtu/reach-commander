using System.Text;
using ReachCommander.Application.Uploads;
using ReachCommander.Infrastructure.FileOperations;

namespace ReachCommander.Infrastructure.Uploads;

internal sealed class UploadFilenameValidator
{
    private const int MaximumUtf8Bytes = 255;
    private const string InvalidCharacters = "<>:\"/\\|?*";

    private static readonly HashSet<string> ReservedDeviceNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase);

    public string Validate(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) ||
            fileName is "." or ".." ||
            fileName.EndsWith('.') ||
            fileName.EndsWith(' ') ||
            Encoding.UTF8.GetByteCount(fileName) > MaximumUtf8Bytes ||
            fileName.Any(IsInvalidCharacter) ||
            IsReservedDeviceName(fileName) ||
            ReservedFileOperationPathPolicy.IsReservedName(fileName))
        {
            throw new UploadNameInvalidException(fileName);
        }

        return fileName;
    }

    private static bool IsInvalidCharacter(char character) =>
        char.IsControl(character) || InvalidCharacters.Contains(character, StringComparison.Ordinal);

    private static bool IsReservedDeviceName(string fileName)
    {
        var extensionIndex = fileName.IndexOf('.');
        var stem = (extensionIndex < 0 ? fileName : fileName[..extensionIndex]).TrimEnd(' ', '.');
        return ReservedDeviceNames.Contains(stem);
    }
}
