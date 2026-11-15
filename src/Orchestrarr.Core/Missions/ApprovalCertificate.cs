using System.Security.Cryptography;
using Orchestrarr.Core.SecondFactor;

namespace Orchestrarr.Core.Missions;

/// <summary>Whether a certificate is for one problem, or for a standing rule that covers a class of problems (FR-PLAN-4, FR-SAF-12).</summary>
public enum CertificateKind
{
    Problem,
    Rule,
}

/// <summary>
/// What the maintainer approved, as the **approval authority** states it (ADR 0031 point 7): the problem (or the rule), the repositories, the highest world, the budget and the expiry. It is the
/// operator's ticked steps made checkable: a runner accepts a mandate only if it falls inside it. It is signed by the authority, which holds the second-factor secret; the control plane does not.
/// </summary>
public sealed record ApprovalCertificate(
    string Id,
    CertificateKind Kind,
    string Subject,
    string ApplicationId,
    IReadOnlyList<string> Repositories,
    World MaxWorld,
    MissionBudget Budget,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Approver,
    string Nonce)
{
    /// <summary>The bytes that are signed: every field, in a fixed order, so that none can move or be replaced without breaking the signature.</summary>
    public byte[] CanonicalBytes()
    {
        var writer = new CanonicalWriter();
        writer.Text(Id);
        writer.Int((int)Kind);
        writer.Text(Subject);
        writer.Text(ApplicationId);
        writer.Int(Repositories.Count);
        foreach (var repository in Repositories.Order(StringComparer.Ordinal))
        {
            writer.Text(repository);
        }
        writer.Int((int)MaxWorld);
        writer.Int(Budget.MaxMinutes);
        writer.Int(Budget.MaxAttempts);
        writer.Int(Budget.MaxTurns);
        writer.Seconds(IssuedAt);
        writer.Seconds(ExpiresAt);
        writer.Text(Approver);
        writer.Text(Nonce);
        return writer.ToArray();
    }
}

public sealed record SignedApproval(ApprovalCertificate Certificate, byte[] Signature);

/// <summary>What the maintainer asks the authority to certify.</summary>
public sealed record ApprovalRequest(CertificateKind Kind, string Subject, string ApplicationId, IReadOnlyList<string> Repositories, World MaxWorld, MissionBudget Budget, TimeSpan Lifetime);

/// <summary>
/// The approval authority (ADR 0031 point 7, ADR 0017 kept): it checks the maintainer's second-factor code with a secret that **the control plane does not hold**, and only then signs a certificate of what
/// was approved. It runs apart from the control plane (its own process, its own key), so that a compromised control plane can issue mandates but not an approval, and a mandate beyond a certificate is
/// refused by every runner.
/// </summary>
public sealed class ApprovalAuthority : IDisposable
{
    private readonly ECDsa _key;
    private readonly SecondFactorGuard _guard;
    private readonly TimeProvider _time;
    private readonly Func<string> _newId;

    public ApprovalAuthority(SecondFactorGuard guard, TimeProvider time, Func<string> newId, ECDsa? key = null)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(newId);
        _guard = guard;
        _time = time;
        _newId = newId;
        _key = key ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }

    /// <summary>The public half, to give to the runners.</summary>
    public byte[] PublicKey() => _key.ExportSubjectPublicKeyInfo();

    /// <summary>The certificate, or null with the reason when the code is not accepted. A request that asks for nothing (no repository, a world above what is asked, no lifetime) is refused before the code is spent.</summary>
    public async Task<(SignedApproval? Approval, SecondFactorResult Result)> ApproveAsync(ApprovalRequest request, string approver, string? code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(approver);
        if (request.Repositories.Count == 0 || !request.Budget.IsValid || request.Lifetime <= TimeSpan.Zero)
        {
            return (null, SecondFactorResult.Malformed);
        }
        var result = await _guard.VerifyAsync(approver, code, cancellationToken);
        if (result != SecondFactorResult.Verified)
        {
            return (null, result);
        }
        var now = _time.GetUtcNow();
        var certificate = new ApprovalCertificate(_newId(), request.Kind, request.Subject, request.ApplicationId, request.Repositories, request.MaxWorld, request.Budget, now, now + request.Lifetime, approver, _newId());
        return (new SignedApproval(certificate, _key.SignData(certificate.CanonicalBytes(), HashAlgorithmName.SHA256)), result);
    }

    public void Dispose() => _key.Dispose();
}

public enum ApprovalRefusal
{
    None,
    Missing,
    BadSignature,
    NotYetValid,
    Expired,
    NotTheApprovalOfThisMandate,
    WrongProblem,
    WorldAboveApproval,
    RepositoryNotApproved,
    BudgetAboveApproval,
    OutlivesApproval,
}

/// <summary>
/// What a runner (and the conductor, before it signs) checks about the certificate that comes with a mandate: it is the authority's (its public key), it is valid now, it is the one the mandate names, it is for
/// this problem (or a rule), and **the mandate falls inside it**: no higher world, no other repository, no bigger budget, no later expiry.
/// </summary>
public sealed class ApprovalVerifier : IDisposable
{
    private readonly ECDsa _publicKey;
    private readonly TimeProvider _time;

    public ApprovalVerifier(ReadOnlySpan<byte> authorityPublicKey, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _publicKey = ECDsa.Create();
        _publicKey.ImportSubjectPublicKeyInfo(authorityPublicKey, out _);
        _time = time;
    }

    public ApprovalRefusal Check(SignedApproval? approval, Mandate mandate)
    {
        ArgumentNullException.ThrowIfNull(mandate);
        if (approval is null)
        {
            return ApprovalRefusal.Missing;
        }
        var certificate = approval.Certificate;
        if (!_publicKey.VerifyData(certificate.CanonicalBytes(), approval.Signature, HashAlgorithmName.SHA256))
        {
            return ApprovalRefusal.BadSignature;
        }
        var now = _time.GetUtcNow();
        if (now < certificate.IssuedAt)
        {
            return ApprovalRefusal.NotYetValid;
        }
        if (now >= certificate.ExpiresAt)
        {
            return ApprovalRefusal.Expired;
        }
        if (!string.Equals(certificate.Id, mandate.ApprovalId, StringComparison.Ordinal))
        {
            return ApprovalRefusal.NotTheApprovalOfThisMandate;
        }
        if (certificate.Kind == CertificateKind.Problem && !string.Equals(certificate.Subject, mandate.ProblemId, StringComparison.Ordinal))
        {
            return ApprovalRefusal.WrongProblem;
        }
        if (mandate.World > certificate.MaxWorld)
        {
            return ApprovalRefusal.WorldAboveApproval;
        }
        if (!mandate.Frame.Repositories.All(repository => certificate.Repositories.Contains(repository, StringComparer.Ordinal)))
        {
            return ApprovalRefusal.RepositoryNotApproved;
        }
        if (mandate.Budget.MaxMinutes > certificate.Budget.MaxMinutes || mandate.Budget.MaxAttempts > certificate.Budget.MaxAttempts || mandate.Budget.MaxTurns > certificate.Budget.MaxTurns)
        {
            return ApprovalRefusal.BudgetAboveApproval;
        }
        return mandate.ExpiresAt > certificate.ExpiresAt ? ApprovalRefusal.OutlivesApproval : ApprovalRefusal.None;
    }

    public void Dispose() => _publicKey.Dispose();
}
