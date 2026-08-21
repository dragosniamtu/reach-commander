namespace ReachCommander.Application.Authentication;

public enum AdministratorAccountState
{
    SetupRequired,
    AccountExists,
}

public sealed record AdministratorIdentity(string Username, string SecurityStamp);

public sealed record CreateAdministratorCommand(
    string SetupCode,
    string Username,
    string Password);

public sealed record ChangeAdministratorPasswordCommand(
    string Username,
    string CurrentPassword,
    string NewPassword);
