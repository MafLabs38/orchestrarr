namespace Orchestrarr.Host.Approvals;

public enum ApprovalsMode
{
    /// <summary>Nobody can approve: the interface shows the cards and says so.</summary>
    Disabled,

    /// <summary>A single, fixed operator with no sign-in, for the local sandbox only (see <see cref="SandboxGuard"/>).</summary>
    Development,

    /// <summary>The person signed in through the identity provider (OpenID Connect), who is an operator when they belong to the configured group.</summary>
    OpenIdConnect,
}
