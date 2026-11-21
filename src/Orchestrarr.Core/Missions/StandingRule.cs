namespace Orchestrarr.Core.Missions;

/// <summary>
/// The power in effect for a step is the **intersection** of the plan entry (which carries the maximum world of the certificate, a standing rule being a certificate of kind
/// <see cref="CertificateKind.Rule"/>) and the per-application grant (D-10, D-11, D-53: off until the maintainer enables it): the lowest of the two. **A grant that is not given is no power at all.**
/// </summary>
public static class EffectivePower
{
    public static World? Of(World approved, World? grant)
    {
        if (grant is null)
        {
            return null;
        }
        return approved < grant ? approved : grant.Value;
    }
}
