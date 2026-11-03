using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Orchestrarr.Host.SignIn;

/// <summary>
/// Signing in through the identity provider (OpenID Connect, SPEC D-40, D-54): the host runs the authorization code flow with PKCE as a
/// confidential client and keeps the session in a cookie. No token reaches the browser. This is what the configuration says; what the host
/// uses is the <see cref="SignInSettings"/> that <see cref="Validate"/> makes of it.
/// </summary>
public sealed class SignInOptions
{
    public const string SectionName = "SignIn";

    private static readonly string[] DefaultScopes = ["openid", "profile"];

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

    /// <summary>
    /// The group whose members are operators: the only ones who can approve (SPEC D-47). It is compared as the claim carries it, so a provider
    /// that sends the full path of a group (Keycloak, with "full path" on) is configured with it: <c>/orchestrarr-operators</c>.
    /// </summary>
    public string OperatorGroup { get; set; } = "orchestrarr-operators";

    /// <summary>A group whose members may read, without being operators. Unset: anybody who signed in may read.</summary>
    public string? ReaderGroup { get; set; }

    /// <summary>
    /// What the host asks of the provider (default: <c>openid</c> and <c>profile</c>; a list here replaces it). The groups must reach the session:
    /// some providers only send them for a scope of their own (add it here), or at the user-information endpoint (see <see cref="ClaimsFromUserInfo"/>).
    /// </summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>
    /// Read the groups (and the username) of the person at the provider's user-information endpoint too, during the sign-in. The access token is used for that
    /// call only and is not kept. What the handler maps by default also applies (<c>sub</c>, <c>name</c>, <c>given_name</c>, <c>family_name</c>, <c>profile</c>,
    /// <c>email</c>, added unless the identity token carried the same value (when they differ, both are kept and the identity token's comes first)), and the authentication context is never taken from there.
    /// </summary>
    public bool ClaimsFromUserInfo { get; set; }

    /// <summary>The claim of the identity token that lists the groups of the person.</summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>The authentication context a second factor gives (<c>acr</c>). Unset: nobody ever counts as having passed one.</summary>
    public string? StepUpAcr { get; set; }

    /// <summary>How long a session lasts, in hours (1 to 24), from the sign-in; it is not extended by use.</summary>
    public int SessionHours { get; set; } = 8;

    /// <summary>A folder that keeps the keys that protect the session cookie, so that a restart or another replica still reads it.</summary>
    public string? KeysDirectory { get; set; }

    /// <summary>
    /// Checked when the host starts. Everything is refused unless it is safe: an address in clear text is only for the loopback of a
    /// development machine, and a value that is missing stops the host instead of leaving a door open.
    /// </summary>
    public SignInSettings Validate(bool allowCleartextOnPrivateNetwork)
    {
        if (!IsAbsolute(Authority, out var authority) || !AllowsScheme(authority, allowCleartextOnPrivateNetwork))
        {
            throw new InvalidOperationException("SignIn:Authority must be an https address (http only on the loopback or a private network address, in Development).");
        }
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new InvalidOperationException("SignIn:ClientId and SignIn:ClientSecret are required.");
        }
        if (!IsAbsolute(PublicUrl, out var origin) || !AllowsScheme(origin, allowCleartextOnPrivateNetwork) || origin.PathAndQuery != "/" || origin.UserInfo.Length > 0 || origin.Fragment.Length > 0)
        {
            throw new InvalidOperationException("SignIn:PublicUrl must be an origin: https://host[:port] (http only on the loopback or a private network address, in Development).");
        }
        if (!IsGroup(OperatorGroup) || (ReaderGroup is not null && !IsGroup(ReaderGroup)) || string.IsNullOrWhiteSpace(GroupsClaim))
        {
            throw new InvalidOperationException("SignIn:OperatorGroup, SignIn:ReaderGroup and SignIn:GroupsClaim must be set, and a group has no space in it and is not just a slash.");
        }
        var scopes = Scopes.Count == 0 ? DefaultScopes : [.. Scopes.Distinct()];
        if (!scopes.Contains("openid") || scopes.Any(scope => string.IsNullOrWhiteSpace(scope) || scope.Any(char.IsWhiteSpace)))
        {
            throw new InvalidOperationException("SignIn:Scopes must include openid, and a scope has no space in it.");
        }
        if (SessionHours is < 1 or > 24)
        {
            throw new InvalidOperationException("SignIn:SessionHours must be between 1 and 24.");
        }

        return new SignInSettings(authority, ClientId, ClientSecret, origin, OperatorGroup, ReaderGroup, GroupsClaim, StepUpAcr, TimeSpan.FromHours(SessionHours), KeysDirectory, [.. scopes], ClaimsFromUserInfo);
    }

    private static bool IsAbsolute(string? value, [NotNullWhen(true)] out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri);

    // https, or, for a development machine, http to the loopback or to an address of a private network (an address, never a name: a name can lead anywhere)
    private static bool AllowsScheme(Uri uri, bool allowCleartextOnPrivateNetwork) =>
        uri.Scheme == Uri.UriSchemeHttps || (allowCleartextOnPrivateNetwork && uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || IsPrivateNetworkAddress(uri.Host)));

    // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 and the unique local addresses of IPv6
    private static bool IsPrivateNetworkAddress(string host)
    {
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            return false;
        }
        if (address.IsIPv6UniqueLocal)
        {
            return true;
        }
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork && (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168));
    }

    private static bool IsGroup(string? group) => !string.IsNullOrWhiteSpace(group) && !group.Any(char.IsWhiteSpace) && group != "/";
}

/// <summary>What the sign-in uses once the configuration is checked: nothing is missing, and the addresses are parsed once.</summary>
public sealed record SignInSettings(
    Uri Authority,
    string ClientId,
    string ClientSecret,
    Uri Origin,
    string OperatorGroup,
    string? ReaderGroup,
    string GroupsClaim,
    string? StepUpAcr,
    TimeSpan SessionLifetime,
    string? KeysDirectory,
    IReadOnlyList<string> Scopes,
    bool ClaimsFromUserInfo)
{
    // The secret of the client never reaches a log, an exception or a debugger through the text of this value
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Authority = {Authority}, ClientId = {ClientId}, Origin = {Origin}, OperatorGroup = {OperatorGroup}, ReaderGroup = {ReaderGroup}");
        return true;
    }

    /// <summary>The address the identity provider sends the browser back to.</summary>
    public Uri CallbackUrl => new(Origin, SignInPaths.Callback);

    /// <summary>Whether the cookies must travel over a secure connection only.</summary>
    public bool Secure => Origin.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// The name of the session cookie. Over https it carries the <c>__Host-</c> prefix: the browser then only takes it from this host, with
    /// no <c>Domain</c>, which keeps a sibling site of a shared domain from planting its own session on the person.
    /// </summary>
    public string SessionCookieName => Secure ? "__Host-orchestrarr.session" : "orchestrarr.session";
}
