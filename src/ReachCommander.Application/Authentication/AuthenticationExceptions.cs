namespace ReachCommander.Application.Authentication;

public abstract class AuthenticationException : Exception
{
    protected AuthenticationException(
        string code,
        string detail,
        Exception? innerException = null)
        : base(detail, innerException)
    {
        Code = code;
        Detail = detail;
    }

    public string Code { get; }

    public string Detail { get; }
}

public sealed class AuthenticationValidationException : AuthenticationException
{
    public AuthenticationValidationException(string code, string detail)
        : base(code, detail)
    {
    }
}

public sealed class AuthenticationStateUnavailableException : AuthenticationException
{
    public AuthenticationStateUnavailableException(
        string detail = "Authentication state is unavailable.",
        Exception? innerException = null)
        : base("authentication_state_unavailable", detail, innerException)
    {
    }
}

public sealed class AdministratorAlreadyExistsException : AuthenticationException
{
    public AdministratorAlreadyExistsException()
        : base("administrator_exists", "The administrator account already exists.")
    {
    }
}

public sealed class InvalidSetupCodeException : AuthenticationException
{
    public InvalidSetupCodeException()
        : base("setup_failed", "Account setup could not be completed.")
    {
    }
}

public sealed class InvalidCurrentPasswordException : AuthenticationException
{
    public InvalidCurrentPasswordException()
        : base("invalid_credentials", "The supplied credentials are not valid.")
    {
    }
}
