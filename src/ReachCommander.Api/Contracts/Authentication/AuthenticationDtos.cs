using System.ComponentModel.DataAnnotations;

namespace ReachCommander.Api.Contracts.Authentication;

public sealed record AuthSessionResponse(string State, string? Username);

public sealed record AntiforgeryTokenResponse(string RequestToken);

public sealed record SetupAdministratorRequest(
    [Required, StringLength(256, MinimumLength = 1)] string SetupCode,
    [Required, StringLength(64, MinimumLength = 3)] string Username,
    [Required, StringLength(128, MinimumLength = 12)] string Password);

public sealed record LoginRequest(
    [Required, StringLength(64, MinimumLength = 3)] string Username,
    [Required, StringLength(128, MinimumLength = 12)] string Password);

public sealed record ChangePasswordRequest(
    [Required, StringLength(128, MinimumLength = 12)] string CurrentPassword,
    [Required, StringLength(128, MinimumLength = 12)] string NewPassword);
