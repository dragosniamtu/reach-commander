namespace ReachCommander.Infrastructure.FileOperations.Execution;

internal static class NativeMoveErrorClassifier
{
    internal static bool IsCrossDevice(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var nativeCode = exception.HResult & 0xFFFF;
        return OperatingSystem.IsWindows()
            ? nativeCode == 17
            : nativeCode == 18;
    }
}
