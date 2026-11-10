using Microsoft.Extensions.Logging;
using Orchestrarr.Core.Launch;

namespace Orchestrarr.Core.Accounts;

/// <summary>
/// Creates the first administrator at start (ADR 0018): only when there is no account yet, from the name in the settings and the password in
/// the file they name. Without them, an empty Orchestrarr has no way in, and says so. The break-glass setting resets the password of a named
/// account at start. A setting that is wrong stops the start with a message that says which.
/// </summary>
public sealed partial class AccountSeeder(IAccountStore accounts, AccountService service, AuthSettings settings, ILogger<AccountSeeder> logger)
{
    public void Run()
    {
        Reset();
        Seed();
    }

    private void Reset()
    {
        if (settings.ResetPasswordUser is not { } user || settings.ResetPasswordFile is not { } file)
        {
            return;
        }

        try
        {
            service.Reset(user, ReadPassword(file, "reset"));
        }
        catch (AccountRejectedException ex)
        {
            throw new InvalidOperationException($"The password reset of '{user}' was refused: {ex.Message}", ex);
        }

        LogReset(user);
    }

    private void Seed()
    {
        if (accounts.Count() > 0)
        {
            return;
        }

        if (settings.AdminUser is not { } user || settings.AdminPasswordFile is not { } file)
        {
            LogNoAdmin();
            return;
        }

        try
        {
            service.Create(user, Roles.Admin, ReadPassword(file, "first administrator"), AuditEvents.System);
        }
        catch (AccountRejectedException ex)
        {
            throw new InvalidOperationException($"The first administrator was refused: {ex.Message}", ex);
        }

        LogCreated(user);
    }

    private static string ReadPassword(string file, string purpose)
    {
        try
        {
            return File.ReadAllText(file).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The password file of the {purpose} cannot be read: {file}", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "There is no account and no first administrator is configured: set Auth__AdminUser and Auth__AdminPasswordFile, or nobody can sign in.")]
    private partial void LogNoAdmin();

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the first administrator {Name}.")]
    private partial void LogCreated(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The password of {Name} was reset from Auth__ResetPasswordFile. Remove Auth__ResetPasswordUser and Auth__ResetPasswordFile now: while they are set, every start resets it again.")]
    private partial void LogReset(string name);
}
