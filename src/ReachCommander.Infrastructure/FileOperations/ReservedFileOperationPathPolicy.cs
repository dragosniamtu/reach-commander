using ReachCommander.Application.Files;

namespace ReachCommander.Infrastructure.FileOperations;

internal static class ReservedFileOperationPathPolicy
{
    internal const string TrashRootName = ".reachcommander-trash";
    internal const string OperationPrefix = ".reachcommander-operation-";

    internal static bool IsReservedName(string name) =>
        name.Equals(TrashRootName, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(OperationPrefix, StringComparison.OrdinalIgnoreCase);

    internal static bool ContainsReservedSegment(string logicalPath) =>
        logicalPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(IsReservedName);

    internal static void ThrowIfReservedName(string name)
    {
        if (IsReservedName(name))
        {
            throw new InvalidLogicalPathException(
                name,
                "it uses a reserved ReachCommander name");
        }
    }
}
