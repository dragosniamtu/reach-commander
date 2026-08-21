using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachCommander.Application.Authentication;

namespace ReachCommander.Infrastructure.Authentication;

internal sealed class AuthenticationBootstrapHostedService(
    IAdministratorAccountService accountService,
    ILogger<AuthenticationBootstrapHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var setupCode = await accountService.PrepareSetupAsync(cancellationToken);
        if (setupCode is not null)
        {
            logger.LogWarning(
                "ReachCommander first-run setup code: {SetupCode}",
                setupCode);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
