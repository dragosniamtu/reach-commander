namespace ReachCommander.ArchiveWorker;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            var dispatcher = new WorkerRequestDispatcher(new SharpCompressArchiveAdapter());
            await dispatcher.DispatchAsync(
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch
        {
            await Console.Error.WriteAsync("worker-failed").ConfigureAwait(false);
            return 1;
        }
    }
}
