using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Host.Accounts;

/// <summary>Whether the session cookie is sent over HTTPS only.</summary>
public enum CookieSecurityMode
{
    /// <summary>The default: the cookie is <c>Secure</c> and carries the <c>__Host-</c> prefix.</summary>
    Always,

    /// <summary>For a plain-HTTP sandbox on a private network: the cookie is <c>Secure</c> only if the request was.</summary>
    SameAsRequest,
}

/// <summary>Settings of the access control, from the <c>Auth</c> section (ADR 0018). The secrets are files, never values.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    public const int MaxSessionHours = 720;
    public const int MaxSessionDays = 365;

    /// <summary>The first administrator, created at start when there is no account yet.</summary>
    public string? AdminUser { get; set; }

    /// <summary>The file that holds the password of the first administrator (a secret mounted as a file, never an environment variable).</summary>
    public string? AdminPasswordFile { get; set; }

    /// <summary>Break-glass: this account gets the password in <see cref="ResetPasswordFile"/>, is enabled, loses its lockout and its sessions, at every start while both are set.</summary>
    public string? ResetPasswordUser { get; set; }

    public string? ResetPasswordFile { get; set; }

    public CookieSecurityMode CookieSecurity { get; set; } = CookieSecurityMode.Always;

    /// <summary>How long a session lasts without being used, in hours.</summary>
    public int SessionHours { get; set; } = 12;

    /// <summary>How long a session lasts at most, used or not, in days.</summary>
    public int SessionMaxDays { get; set; } = 30;

    /// <summary>A folder that keeps the keys that protect the session cookie, so that a restart still reads it. Unset: next to the database when there is one, else in memory.</summary>
    public string? KeysDirectory { get; set; }

    /// <summary>Checked when the host starts: a value that is missing or wrong stops the host instead of leaving a door open.</summary>
    public AuthConfig Validate()
    {
        if (SessionHours is < 1 or > MaxSessionHours)
        {
            throw new InvalidOperationException($"Auth:SessionHours must be between 1 and {MaxSessionHours}.");
        }
        if (SessionMaxDays is < 1 or > MaxSessionDays)
        {
            throw new InvalidOperationException($"Auth:SessionMaxDays must be between 1 and {MaxSessionDays}.");
        }
        if (string.IsNullOrWhiteSpace(AdminUser) != string.IsNullOrWhiteSpace(AdminPasswordFile))
        {
            throw new InvalidOperationException("Auth:AdminUser and Auth:AdminPasswordFile are set together or not at all.");
        }
        if (string.IsNullOrWhiteSpace(ResetPasswordUser) != string.IsNullOrWhiteSpace(ResetPasswordFile))
        {
            throw new InvalidOperationException("Auth:ResetPasswordUser and Auth:ResetPasswordFile are set together or not at all.");
        }
        if (!Enum.IsDefined(CookieSecurity))
        {
            throw new InvalidOperationException("Auth:CookieSecurity must be Always or SameAsRequest.");
        }

        var settings = new AuthSettings(Clean(AdminUser), Clean(AdminPasswordFile), Clean(ResetPasswordUser), Clean(ResetPasswordFile), TimeSpan.FromHours(SessionHours), TimeSpan.FromDays(SessionMaxDays));
        return new AuthConfig(settings, CookieSecurity == CookieSecurityMode.Always, Clean(KeysDirectory));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>What the host uses once the <c>Auth</c> settings are checked.</summary>
/// <param name="Settings">What the accounts need.</param>
/// <param name="SecureCookie">The cookie is sent over HTTPS only (and the host tells the browser to keep to it).</param>
/// <param name="KeysDirectory">Where the keys of the cookie are kept, when set.</param>
public sealed record AuthConfig(AuthSettings Settings, bool SecureCookie, string? KeysDirectory);
