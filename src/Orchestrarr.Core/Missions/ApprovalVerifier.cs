using System.Security.Cryptography;

namespace Orchestrarr.Core.Missions;

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
    WrongApplication,
    WrongClass,
    ActsOfAnotherWorld,
    ForbiddenPathsDropped,

    /// <summary>The certificate's kind or highest world is not one that exists: it would bind no problem and no class.</summary>
    UnknownKind,
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
        // A kind or a world that is not one of the defined ones is no certificate: every later check is written for the two kinds that exist
        if (!Enum.IsDefined(certificate.Kind) || !Enum.IsDefined(certificate.MaxWorld))
        {
            return ApprovalRefusal.UnknownKind;
        }
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
        if (certificate.Kind == CertificateKind.Rule && !string.Equals(certificate.Subject, mandate.ProblemClass, StringComparison.Ordinal))
        {
            return ApprovalRefusal.WrongClass;
        }
        if (!string.Equals(certificate.ApplicationId, mandate.ApplicationId, StringComparison.Ordinal))
        {
            return ApprovalRefusal.WrongApplication;
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
        if (!mandate.Acts.All(act => WorldActs.Of(mandate.World).Contains(act)))
        {
            return ApprovalRefusal.ActsOfAnotherWorld;
        }
        if (!certificate.ForbiddenPaths.All(path => mandate.Frame.ForbiddenPaths.Contains(path, StringComparer.Ordinal)))
        {
            return ApprovalRefusal.ForbiddenPathsDropped;
        }
        return mandate.ExpiresAt > certificate.ExpiresAt ? ApprovalRefusal.OutlivesApproval : ApprovalRefusal.None;
    }

    public void Dispose() => _publicKey.Dispose();
}
