using System.Diagnostics.CodeAnalysis;

namespace Orchestrarr.Host.SignIn;

/// <summary>Where a person goes after signing in: a path of this application, never an address that leads elsewhere.</summary>
internal static class ReturnUrl
{
    /// <summary>The path asked for when it is one of this application, otherwise the start page.</summary>
    public static string Safe(string? value) => IsLocalPath(value) ? value : SignInPaths.Home;

    // "/", "/status", "/decisions?x=1". Not "//host" nor "/\host" (read as another site), not a scheme, not a control character.
    private static bool IsLocalPath([NotNullWhen(true)] string? value) =>
        value is { Length: > 0 and <= 2000 }
        && value[0] == '/'
        && (value.Length == 1 || (value[1] != '/' && value[1] != '\\'))
        && !value.Any(char.IsControl);
}
