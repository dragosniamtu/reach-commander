using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachCommander.Infrastructure.FileOperations.Execution;
using ReachCommander.Infrastructure.FileOperations.Persistence;

namespace ReachCommander.Infrastructure.FileOperations;

internal sealed class FileOperationWorker(
    FileOperationRepository repository,
    FileOperationQueue queue,
    FileOperationJobDispatcher dispatcher,
    InterruptedOperationCleaner cleaner,
    ILogger<FileOperationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await repository.RecoverAsync(stoppingToken);
        await cleaner.CleanRecoveredOperationsAsync(stoppingToken);
        queue.Signal();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await queue.WaitAsync(stoppingToken);
                PersistedFileOperationDocument? job;
                while ((job = await repository.TryTakeNextAsync(stoppingToken)) is not null)
                {
                    try
                    {
                        var status = await dispatcher.DispatchAsync(job, stoppingToken);
                        logger.LogInformation(
                            "File operation {OperationId} reached phase {Phase}",
                            status.OperationId,
                            status.Phase);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(
                            "File operation {OperationId} dispatcher failed with {ExceptionType}",
                            job.OperationId,
                            exception.GetType().Name);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
