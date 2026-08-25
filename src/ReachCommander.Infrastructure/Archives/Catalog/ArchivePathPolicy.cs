using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Archives;
using ReachCommander.Infrastructure.FileOperations;

namespace ReachCommander.Infrastructure.Archives.Catalog;

internal sealed partial class ArchivePathPolicy(IOptions<ArchiveOptions> options)
{
    private static readonly char[] PortableInvalidCharacters = ['<', '>', '"', '|', '?', '*', ':'];
    private readonly ArchiveOptions _options = options.Value;

    public string NormalizeEntryPath(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Contains('\0') ||
            value.StartsWith("/", StringComparison.Ordinal) ||
            value.StartsWith("\\", StringComparison.Ordinal) ||
            DrivePathRegex().IsMatch(value))
        {
            throw new ArchiveEntryUnsafeException();
        }

        var normalizedSeparators = value.Replace('\\', '/');
        if (normalizedSeparators.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArchiveEntryUnsafeException();
        }

        var components = normalizedSeparators.Split('/', StringSplitOptions.None);
        if (components.Length > _options.MaxPathDepth)
        {
            throw new ArchiveLimitExceededException(
                "An archive entry exceeds the configured path-depth limit.");
        }

        var normalizedComponents = new string[components.Length];
        for (var index = 0; index < components.Length; index++)
        {
            normalizedComponents[index] = NormalizeComponent(components[index]);
        }

        var result = $"/{string.Join('/', normalizedComponents)}";
        if (result.Length > _options.MaxPathCharacters)
        {
            throw new ArchiveLimitExceededException(
                "An archive entry exceeds the configured path-length limit.");
        }

        return result;
    }

    private string NormalizeComponent(string value)
    {
        if (string.IsNullOrEmpty(value) || value is "." or "..")
        {
            throw new ArchiveEntryUnsafeException();
        }

        var normalized = value.Normalize(NormalizationForm.FormC);
        if (normalized.Length > _options.MaxComponentCharacters)
        {
            throw new ArchiveLimitExceededException(
                "An archive entry exceeds the configured component-length limit.");
        }

        if (normalized.Any(char.IsControl) ||
            normalized.IndexOfAny(PortableInvalidCharacters) >= 0 ||
            normalized.Contains('/') ||
            normalized.Contains('\\') ||
            normalized.EndsWith(".", StringComparison.Ordinal) ||
            normalized.EndsWith(" ", StringComparison.Ordinal) ||
            IsReservedDeviceName(normalized) ||
            IsStagingControlName(normalized))
        {
            throw new ArchiveEntryUnsafeException();
        }

        return normalized;
    }

    private static bool IsReservedDeviceName(string component)
    {
        var dot = component.IndexOf('.');
        var baseName = dot < 0 ? component : component[..dot];
        return ReservedDeviceRegex().IsMatch(baseName);
    }

    private static bool IsStagingControlName(string component) =>
        component.Equals(".reachcommander-owner", StringComparison.OrdinalIgnoreCase) ||
        ReservedFileOperationPathPolicy.IsReservedName(component) ||
        (component.StartsWith(".reachcommander-extract-", StringComparison.OrdinalIgnoreCase) &&
         component.EndsWith(".partial", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^/?[A-Za-z]:[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex DrivePathRegex();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDeviceRegex();
}
