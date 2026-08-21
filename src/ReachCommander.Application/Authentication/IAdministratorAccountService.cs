namespace ReachCommander.Application.Authentication;

public interface IAdministratorAccountService
{
    Task<string?> PrepareSetupAsync(CancellationToken cancellationToken);

    Task<AdministratorAccountState> GetStateAsync(CancellationToken cancellationToken);

    Task<AdministratorIdentity> CreateAsync(
        CreateAdministratorCommand command,
        CancellationToken cancellationToken);

    Task<AdministratorIdentity?> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken);

    Task<bool> ValidateSessionAsync(
        string username,
        string securityStamp,
        CancellationToken cancellationToken);

    Task<AdministratorIdentity> ChangePasswordAsync(
        ChangeAdministratorPasswordCommand command,
        CancellationToken cancellationToken);
}
