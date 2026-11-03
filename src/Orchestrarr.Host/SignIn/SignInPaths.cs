namespace Orchestrarr.Host.SignIn;

/// <summary>The addresses of the sign-in. <see cref="Failed"/> is also written in <c>frontend/src/Helpers/signIn.ts</c>: the interface reads what the host sends.</summary>
internal static class SignInPaths
{
    public const string Login = "/auth/login";
    public const string Logout = "/auth/logout";
    public const string Callback = "/auth/callback";
    public const string SignedOut = "/auth/signed-out";
    public const string Home = "/";
    public const string Api = "/api";

    /// <summary>Where a sign-in that failed sends the browser: the interface says so.</summary>
    public const string Failed = "/?signInFailed=1";
}
