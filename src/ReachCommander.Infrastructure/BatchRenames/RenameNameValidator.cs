using System.Text;
using System.Text.RegularExpressions;
using ReachCommander.Infrastructure.FileOperations;

namespace ReachCommander.Infrastructure.BatchRenames;

internal sealed record RenameNameValidation(bool IsValid, string? Message);

internal sealed partial class RenameNameValidator
{
    private const int MaximumUtf8Bytes = 255;
    private const string PortableInvalidCharacters = "<>:\"/\\|?*";

    public RenameNameValidation Validate(string completeName)
    {
        if (string.IsNullOrEmpty(completeName) || completeName is "." or "..")
        {
            return Invalid("A filename cannot be empty, '.' or '..'.");
        }

        if (completeName.Any(character =>
                char.IsControl(character) ||
                PortableInvalidCharacters.Contains(character, StringComparison.Ordinal)))
        {
            return Invalid("The filename contains a forbidden character.");
        }

        if (completeName.EndsWith('.') || completeName.EndsWith(' '))
        {
            return Invalid("A filename cannot end with a dot or space.");
        }

        if (ReservedDeviceName().IsMatch(completeName))
        {
            return Invalid("The filename is reserved by Windows.");
        }

        if (ReservedFileOperationPathPolicy.IsReservedName(completeName))
        {
            return Invalid("The filename is reserved by ReachCommander.");
        }

        if (Encoding.UTF8.GetByteCount(completeName) > MaximumUtf8Bytes)
        {
            return Invalid("The filename exceeds the 255-byte component limit.");
        }

        return new RenameNameValidation(true, null);
    }

    private static RenameNameValidation Invalid(string message) => new(false, message);

    [GeneratedRegex(
        "^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDeviceName();
}
