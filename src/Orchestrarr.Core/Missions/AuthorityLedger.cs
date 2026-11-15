namespace Orchestrarr.Core.Missions;

/// <summary>The authority could not be asked (the count of a certificate, its withdrawal): a runner that cannot ask refuses, the same rule as the stop.</summary>
public sealed class AuthorityUnreachableException : Exception
{
    public AuthorityUnreachableException()
        : base("The approval authority cannot be reached.")
    {
    }

    public AuthorityUnreachableException(string message)
        : base(message)
    {
    }

    public AuthorityUnreachableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// What the authority keeps, in **one place for every runner**: the certificates it withdrew and how many mandates each one backed. A count kept by each runner would let a compromised control plane
/// spread mandates over the runners and exceed it. The authority runs apart and keeps this in its own store; the in-memory one below is for tests and for a first start.
/// </summary>
public interface IAuthorityLedger
{
    void Revoke(string certificateId);

    bool IsRevoked(string certificateId);

    IReadOnlyCollection<string> Revoked();

    /// <summary>Counts one mandate, once per nonce (asking again for the same nonce answers the same), against <paramref name="maxMandates"/>.</summary>
    MandateUse Use(string certificateId, string nonce, int maxMandates);
}

public sealed class InMemoryAuthorityLedger : IAuthorityLedger
{
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _uses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Revoke(string certificateId)
    {
        lock (_gate)
        {
            _revoked.Add(certificateId);
        }
    }

    public bool IsRevoked(string certificateId)
    {
        lock (_gate)
        {
            return _revoked.Contains(certificateId);
        }
    }

    public IReadOnlyCollection<string> Revoked()
    {
        lock (_gate)
        {
            return [.. _revoked];
        }
    }

    public MandateUse Use(string certificateId, string nonce, int maxMandates)
    {
        lock (_gate)
        {
            // A nonce is used once whatever the certificate: a second request for it is a replay
            if (!_seen.Add(nonce))
            {
                return MandateUse.Replayed;
            }
            if (!_uses.TryGetValue(certificateId, out var used))
            {
                used = new HashSet<string>(StringComparer.Ordinal);
                _uses[certificateId] = used;
            }
            if (used.Count >= maxMandates)
            {
                _seen.Remove(nonce);
                return MandateUse.ApprovalUsedUp;
            }
            used.Add(nonce);
            return MandateUse.Used;
        }
    }
}
