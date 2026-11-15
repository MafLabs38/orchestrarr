using System.Security.Cryptography;

namespace Orchestrarr.Core.Missions;

/// <summary>
/// Signs mandates with an **asymmetric** key whose private half only the control plane holds (ADR 0031): a runner that is taken (a staging runner, for example) holds
/// only the public half, so it cannot forge a mandate for another world.
/// </summary>
public sealed class MandateSigner : IDisposable
{
    private readonly ECDsa _key;

    private MandateSigner(ECDsa key) => _key = key;

    /// <summary>A new key pair, for tests and for a first start.</summary>
    public static MandateSigner Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>The key of the control plane, from its private key (PKCS#8).</summary>
    public static MandateSigner FromPrivateKey(ReadOnlySpan<byte> pkcs8)
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(pkcs8, out _);
        return new MandateSigner(key);
    }

    /// <summary>The public half, to give to the runners (SubjectPublicKeyInfo).</summary>
    public byte[] PublicKey() => _key.ExportSubjectPublicKeyInfo();

    /// <summary>Whether this signer signed the mandate: the control plane checks what it stored before it authorises an act on it.</summary>
    public bool Verify(SignedMandate signed)
    {
        ArgumentNullException.ThrowIfNull(signed);
        return _key.VerifyData(signed.Mandate.CanonicalBytes(), signed.Signature, HashAlgorithmName.SHA256);
    }

    public byte[] PrivateKey() => _key.ExportPkcs8PrivateKey();

    public SignedMandate Sign(Mandate mandate, SignedApproval? approval = null)
    {
        ArgumentNullException.ThrowIfNull(mandate);
        return new SignedMandate(mandate, _key.SignData(mandate.CanonicalBytes(), HashAlgorithmName.SHA256), approval);
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>Why a runner refuses a mandate.</summary>
public enum MandateRefusal
{
    None,
    BadSignature,
    WrongRunner,
    WrongWorld,
    NotYetValid,
    Expired,
    Replayed,
    Revoked,
    KillSwitchEngaged,
    ActNotAllowed,

    /// <summary>The mandate has no certificate of the approval authority, or one that does not cover it (see <see cref="ApprovalRefusal"/>).</summary>
    NoValidApproval,

    /// <summary>The authority withdrew the certificate before its expiry.</summary>
    ApprovalRevoked,

    /// <summary>The certificate already backed as many mandates as it allows: the control plane cannot sign more under it.</summary>
    ApprovalUsedUp,

    /// <summary>The authority could not be asked about the certificate (its count, its withdrawal): the runner refuses, as it does when the stop cannot be read.</summary>
    AuthorityUnreachable,
}

/// <summary>What recording a mandate's nonce gave.</summary>
public enum MandateUse
{
    Used,
    Replayed,
    ApprovalUsedUp,
}

/// <summary>What a runner knows about a mandate beyond its content: whether it was already used, revoked, and whether the global stop is engaged.</summary>
public interface IMandateState
{
    /// <summary>
    /// Records the nonce and counts the mandate against its certificate: a replay is refused, and so is a mandate beyond <paramref name="maxMandates"/> for the same certificate. **The count is the authority's, one for every runner**
    /// (<see cref="ApprovalAuthority.Use"/>): it throws <see cref="AuthorityUnreachableException"/> when the authority cannot be asked, and the runner then refuses.
    /// </summary>
    MandateUse TryUse(string nonce, string approvalId, int maxMandates);

    /// <summary>Whether the authority withdrew this certificate (<see cref="ApprovalAuthority.IsRevoked"/>); throws <see cref="AuthorityUnreachableException"/> when it cannot be asked.</summary>
    bool IsApprovalRevoked(string approvalId);

    bool IsRevoked(string mandateId);

    bool KillSwitchEngaged { get; }
}

/// <summary>
/// The runner's side (FUNCTIONAL 5.5, ADR 0031): it holds only the public key, checks the signature, that the mandate is for **this runner** and **this world**, within its
/// validity, never used before, not revoked and that the global stop is not engaged; and, before **every gated act**, that the act is listed and the mandate is still good.
/// </summary>
public sealed class MandateVerifier : IDisposable
{
    private readonly ECDsa _publicKey;
    private readonly string _runnerId;
    private readonly World _world;
    private readonly IMandateState _state;
    private readonly TimeProvider _time;
    private readonly ApprovalVerifier _approvals;

    /// <summary>The runner's side: the public key of the control plane, the runner and the world it serves, what it remembers, the clock, and the verifier of the authority's certificates, which is required: a runner that checks no approval would accept any mandate the control plane signed.</summary>
    public MandateVerifier(ReadOnlySpan<byte> publicKey, string runnerId, World world, IMandateState state, TimeProvider time, ApprovalVerifier approvals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerId);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(approvals);
        _publicKey = ECDsa.Create();
        _publicKey.ImportSubjectPublicKeyInfo(publicKey, out _);
        _runnerId = runnerId;
        _world = world;
        _state = state;
        _time = time;
        _approvals = approvals;
    }

    /// <summary>Starts a mission: every check, and the nonce is used up. A mandate is single-use.</summary>
    public MandateRefusal Accept(SignedMandate signed)
    {
        var refusal = Check(signed);
        if (refusal != MandateRefusal.None)
        {
            return refusal;
        }
        if (signed.Approval is not { } approval)
        {
            return MandateRefusal.NoValidApproval;
        }
        try
        {
            return _state.TryUse(signed.Mandate.Nonce, signed.Mandate.ApprovalId, approval.Certificate.MaxMandates) switch
            {
                MandateUse.Used => MandateRefusal.None,
                MandateUse.ApprovalUsedUp => MandateRefusal.ApprovalUsedUp,
                _ => MandateRefusal.Replayed,
            };
        }
        catch (AuthorityUnreachableException)
        {
            return MandateRefusal.AuthorityUnreachable;
        }
    }

    /// <summary>Before a gated act: the mandate is still good and lists the act. The nonce is not used again here.</summary>
    public MandateRefusal MayAct(SignedMandate signed, GatedAct act)
    {
        var refusal = Check(signed);
        if (refusal != MandateRefusal.None)
        {
            return refusal;
        }
        return signed.Mandate.Acts.Contains(act) ? MandateRefusal.None : MandateRefusal.ActNotAllowed;
    }

    public void Dispose() => _publicKey.Dispose();

    private MandateRefusal Check(SignedMandate signed)
    {
        ArgumentNullException.ThrowIfNull(signed);
        var mandate = signed.Mandate;
        if (!_publicKey.VerifyData(mandate.CanonicalBytes(), signed.Signature, HashAlgorithmName.SHA256))
        {
            return MandateRefusal.BadSignature;
        }
        if (_approvals.Check(signed.Approval, mandate) != ApprovalRefusal.None)
        {
            return MandateRefusal.NoValidApproval;
        }
        try
        {
            if (_state.IsApprovalRevoked(mandate.ApprovalId))
            {
                return MandateRefusal.ApprovalRevoked;
            }
        }
        catch (AuthorityUnreachableException)
        {
            return MandateRefusal.AuthorityUnreachable;
        }
        var now = _time.GetUtcNow();
        if (!string.Equals(mandate.RunnerId, _runnerId, StringComparison.Ordinal))
        {
            return MandateRefusal.WrongRunner;
        }
        if (mandate.World != _world)
        {
            return MandateRefusal.WrongWorld;
        }
        if (now < mandate.IssuedAt)
        {
            return MandateRefusal.NotYetValid;
        }
        if (now >= mandate.ExpiresAt)
        {
            return MandateRefusal.Expired;
        }
        if (_state.KillSwitchEngaged)
        {
            return MandateRefusal.KillSwitchEngaged;
        }
        return _state.IsRevoked(mandate.Id) ? MandateRefusal.Revoked : MandateRefusal.None;
    }
}
