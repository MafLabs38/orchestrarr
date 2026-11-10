namespace Orchestrarr.Core.Accounts;

/// <summary>
/// What the accounts need from the <c>Auth</c> settings, once validated: a value the host reads and nothing changes. Where the settings come
/// from and how they are checked is the host's (<c>AuthOptions</c>).
/// </summary>
/// <param name="AdminUser">The first administrator, created at start when there is no account yet. Set together with <paramref name="AdminPasswordFile"/>.</param>
/// <param name="AdminPasswordFile">The file that holds the password of the first administrator (a secret mounted as a file, never an environment variable).</param>
/// <param name="ResetPasswordUser">Break-glass: this account gets the password in <paramref name="ResetPasswordFile"/> at every start while both are set.</param>
/// <param name="ResetPasswordFile">The file that holds the password of the break-glass reset.</param>
/// <param name="SessionIdle">How long a session lasts without being used.</param>
/// <param name="SessionMax">How long a session lasts at most, used or not.</param>
public sealed record AuthSettings(string? AdminUser, string? AdminPasswordFile, string? ResetPasswordUser, string? ResetPasswordFile, TimeSpan SessionIdle, TimeSpan SessionMax);
