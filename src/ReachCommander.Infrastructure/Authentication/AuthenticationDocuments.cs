namespace ReachCommander.Infrastructure.Authentication;

internal sealed record AdministratorAccountDocument(
    int Version,
    string Username,
    string NormalizedUsername,
    string PasswordHash,
    string SecurityStamp);

internal sealed record BootstrapDocument(
    int Version,
    string Verifier,
    DateTimeOffset CreatedAt);
