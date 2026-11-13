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

    public byte[] PrivateKey() => _key.ExportPkcs8PrivateKey();

    public SignedMandate Sign(Mandate mandate)
    {
        ArgumentNullException.ThrowIfNull(mandate);
        return new SignedMandate(mandate, _key.SignData(mandate.CanonicalBytes(), HashAlgorithmName.SHA256));
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
}

/// <summary>What a runner knows about a mandate beyond its content: whether it was already used, revoked, and whether the global stop is engaged.</summary>
public interface IMandateState
{
    /// <summary>Records the nonce; false if it was already recorded (a replay).</summary>
    bool TryUse(string nonce);

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

    public MandateVerifier(ReadOnlySpan<byte> publicKey, string runnerId, World world, IMandateState state, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerId);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(time);
        _publicKey = ECDsa.Create();
        _publicKey.ImportSubjectPublicKeyInfo(publicKey, out _);
        _runnerId = runnerId;
        _world = world;
        _state = state;
        _time = time;
    }

    /// <summary>Starts a mission: every check, and the nonce is used up. A mandate is single-use.</summary>
    public MandateRefusal Accept(SignedMandate signed)
    {
        var refusal = Check(signed);
        if (refusal != MandateRefusal.None)
        {
            return refusal;
        }
        return _state.TryUse(signed.Mandate.Nonce) ? MandateRefusal.None : MandateRefusal.Replayed;
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
