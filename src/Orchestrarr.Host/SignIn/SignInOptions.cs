namespace Orchestrarr.Host.SignIn;

/// <summary>
/// Signing in through the identity provider (OpenID Connect, SPEC D-40, D-54): the host runs the authorization code flow with PKCE as a
/// confidential client and keeps the session in a cookie. No token reaches the browser.
/// </summary>
public sealed class SignInOptions
{
    public const string SectionName = "SignIn";

    /// <summary>The issuer of the identity provider, for example <c>https://sso.example/realms/example</c>. Its discovery document is read from here.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    /// <summary>The secret of the client. Never in a file of the repository: user secrets locally, a secret of the cluster in production.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Where the browser reaches Orchestrarr, as an origin (<c>https://orchestrarr.example</c>). The addresses sent to the identity provider and the
    /// origin accepted on a change are built from it, never from the <c>Host</c> header of a request.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>The group whose members are operators: the only ones who can approve (SPEC D-47).</summary>
    public string OperatorGroup { get; set; } = "orchestrarr-operators";

    /// <summary>The claim of the identity token that lists the groups of the person.</summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>The authentication context a second factor gives (<c>acr</c>). Unset: nobody ever counts as having passed one.</summary>
    public string? StepUpAcr { get; set; }

    /// <summary>How long a session lasts, from the sign-in; it is not extended by use.</summary>
    public int SessionHours { get; set; } = 8;

    /// <summary>A folder that keeps the keys that protect the session cookie, so that a restart or another replica still reads it. Unset: the keys live in memory.</summary>
    public string? KeysDirectory { get; set; }

    /// <summary>The address the identity provider sends the browser back to.</summary>
    public Uri CallbackUrl => new(Origin, CallbackPath);

    public const string CallbackPath = "/auth/callback";

    /// <summary>The origin the browser reaches Orchestrarr at.</summary>
    public Uri Origin => new(PublicUrl!, UriKind.Absolute);

    /// <summary>Whether the cookies must travel over a secure connection only.</summary>
    public bool Secure => Origin.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// Checked when the host starts. Everything is refused unless it is safe: an address in clear text is only for the loopback of a
    /// development machine, and a value that is missing stops the host instead of leaving a door open.
    /// </summary>
    public void Validate(bool development)
    {
        if (!IsAbsolute(Authority, out var authority) || !AllowsScheme(authority!, development))
        {
            throw new InvalidOperationException("SignIn:Authority must be an https address (http only on the loopback in Development).");
        }
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new InvalidOperationException("SignIn:ClientId and SignIn:ClientSecret are required.");
        }
        if (!IsAbsolute(PublicUrl, out var origin) || !AllowsScheme(origin!, development) || origin!.PathAndQuery != "/" || origin.UserInfo.Length > 0 || origin.Fragment.Length > 0)
        {
            throw new InvalidOperationException("SignIn:PublicUrl must be an origin: https://host[:port] (http only on the loopback in Development).");
        }
        if (string.IsNullOrWhiteSpace(OperatorGroup) || OperatorGroup.Any(char.IsWhiteSpace) || string.IsNullOrWhiteSpace(GroupsClaim))
        {
            throw new InvalidOperationException("SignIn:OperatorGroup and SignIn:GroupsClaim must be set, and the group has no space in it.");
        }
        if (SessionHours is < 1 or > 24)
        {
            throw new InvalidOperationException("SignIn:SessionHours must be between 1 and 24.");
        }
    }

    private static bool IsAbsolute(string? value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri);

    // https, or http for the loopback of a development machine
    private static bool AllowsScheme(Uri uri, bool development) =>
        uri.Scheme == Uri.UriSchemeHttps || (development && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
}
