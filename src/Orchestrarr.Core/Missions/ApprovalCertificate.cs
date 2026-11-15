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
    string Nonce,
    IReadOnlyList<string> ForbiddenPaths,
    int MaxMandates)
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
        writer.Int(ForbiddenPaths.Count);
        foreach (var path in ForbiddenPaths.Order(StringComparer.Ordinal))
        {
            writer.Text(path);
        }
        writer.Int(MaxMandates);
        return writer.ToArray();
    }
}

public sealed record SignedApproval(ApprovalCertificate Certificate, byte[] Signature);

/// <summary>What the maintainer asks the authority to certify. <c>MaxMandates</c> is how many mandates the whole certificate may back, counted by the runners: a certificate for a standing rule would otherwise back any number of problems.</summary>
public sealed record ApprovalRequest(CertificateKind Kind, string Subject, string ApplicationId, IReadOnlyList<string> Repositories, World MaxWorld, MissionBudget Budget, TimeSpan Lifetime, IReadOnlyList<string>? ForbiddenPaths = null, int MaxMandates = 8);

/// <summary>The bounds that the authority puts on what it signs, whoever asks: a certificate lives for a limited time and backs a limited number of mandates (a standing rule lives longer than a problem, but reaches only the Git world: the release world needs a certificate for the problem itself).</summary>
public sealed record ApprovalPolicy(TimeSpan MaxProblemLifetime, TimeSpan MaxRuleLifetime, int MaxMandates, TimeSpan ProposalLifetime, World MaxRuleWorld)
{
    public static ApprovalPolicy Default { get; } = new(TimeSpan.FromDays(7), TimeSpan.FromDays(30), 50, TimeSpan.FromMinutes(10), World.Git);
}

/// <summary>
/// A request the authority holds until the maintainer confirms it **on the authority's own channel**: <c>Summary</c> is what the authority shows and <c>Digest</c> identifies it. The code that
/// confirms it signs exactly this and nothing else, so whoever relays the code cannot choose what is signed.
/// </summary>
public sealed record ApprovalProposal(string Id, string Approver, string Summary, string Digest, DateTimeOffset ExpiresAt);

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
    private readonly ApprovalPolicy _policy;
    private readonly Dictionary<string, (ApprovalProposal Proposal, ApprovalRequest Request)> _proposals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _revoked = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>The authority takes ownership of <paramref name="key"/> when one is given: it disposes it.</summary>
    public ApprovalAuthority(SecondFactorGuard guard, TimeProvider time, Func<string> newId, ECDsa? key = null, ApprovalPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(newId);
        _guard = guard;
        _time = time;
        _newId = newId;
        _key = key ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _policy = policy ?? ApprovalPolicy.Default;
    }

    /// <summary>The public half, to give to the runners.</summary>
    public byte[] PublicKey() => _key.ExportSubjectPublicKeyInfo();

    /// <summary>
    /// The request is held, not signed: the authority shows <see cref="ApprovalProposal.Summary"/> to the maintainer on its own channel, and only a code typed there signs it. A request that asks for nothing
    /// (no repository, no budget, no lifetime) or for more than the policy allows is refused here, before any code is spent. Null: refused.
    /// </summary>
    public ApprovalProposal? Propose(ApprovalRequest request, string approver)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(approver);
        var longest = request.Kind == CertificateKind.Rule ? _policy.MaxRuleLifetime : _policy.MaxProblemLifetime;
        if (request.Repositories.Count == 0 || !request.Budget.IsValid || request.Lifetime <= TimeSpan.Zero || request.Lifetime > longest || request.MaxMandates < 1 || request.MaxMandates > _policy.MaxMandates || (request.Kind == CertificateKind.Rule && request.MaxWorld > _policy.MaxRuleWorld))
        {
            return null;
        }
        var now = _time.GetUtcNow();
        var summary = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{request.Kind} {request.Subject} of {request.ApplicationId}: repositories {string.Join(", ", request.Repositories.Order(StringComparer.Ordinal))}; up to the {request.MaxWorld} world; {request.Budget.MaxMinutes} min, {request.Budget.MaxAttempts} attempts, {request.Budget.MaxTurns} turns; at most {request.MaxMandates} mandates; valid {request.Lifetime.TotalHours:0.#} h; forbidden: {string.Join(", ", (request.ForbiddenPaths ?? []).Order(StringComparer.Ordinal))}");
        var digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(summary)));
        var proposal = new ApprovalProposal(_newId(), approver, summary, digest, now + _policy.ProposalLifetime);
        lock (_gate)
        {
            foreach (var expired in _proposals.Where(entry => entry.Value.Proposal.ExpiresAt <= now).Select(entry => entry.Key).ToList())
            {
                _proposals.Remove(expired);
            }
            _proposals[proposal.Id] = (proposal, request);
        }
        return proposal;
    }

    /// <summary>What the authority shows the maintainer on its own channel for a proposal that is still open.</summary>
    public ApprovalProposal? Describe(string proposalId)
    {
        lock (_gate)
        {
            return _proposals.TryGetValue(proposalId, out var held) && held.Proposal.ExpiresAt > _time.GetUtcNow() ? held.Proposal : null;
        }
    }

    /// <summary>The certificate of the proposal that the code confirms, or null with the reason. The proposal is used up whatever the code says, so that a code cannot be tried again on it.</summary>
    public async Task<(SignedApproval? Approval, SecondFactorResult Result)> ApproveAsync(string proposalId, string approver, string? code, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approver);
        (ApprovalProposal Proposal, ApprovalRequest Request) held;
        lock (_gate)
        {
            if (!_proposals.Remove(proposalId ?? string.Empty, out held) || held.Proposal.ExpiresAt <= _time.GetUtcNow() || !string.Equals(held.Proposal.Approver, approver, StringComparison.Ordinal))
            {
                return (null, SecondFactorResult.Malformed);
            }
        }
        var result = await _guard.VerifyAsync(approver, code, cancellationToken);
        if (result != SecondFactorResult.Verified)
        {
            return (null, result);
        }
        var request = held.Request;
        var now = _time.GetUtcNow();
        var id = _newId();
        var nonce = _newId();
        var certificate = new ApprovalCertificate(id, request.Kind, request.Subject, request.ApplicationId, request.Repositories, request.MaxWorld, request.Budget, now, now + request.Lifetime, approver, nonce, request.ForbiddenPaths ?? [], request.MaxMandates);
        return (new SignedApproval(certificate, _key.SignData(certificate.CanonicalBytes(), HashAlgorithmName.SHA256)), result);
    }

    /// <summary>The maintainer withdraws an approval before its expiry (a standing rule revoked): every runner reads the list and refuses what the certificate backs.</summary>
    public void Revoke(string certificateId)
    {
        lock (_gate)
        {
            _revoked.Add(certificateId);
        }
    }

    /// <summary>The certificates withdrawn so far, for the runners to read (as they read the global stop).</summary>
    public IReadOnlyCollection<string> Revoked()
    {
        lock (_gate)
        {
            return [.. _revoked];
        }
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
    WrongApplication,
    WrongClass,
    ActsOfAnotherWorld,
    ForbiddenPathsDropped,
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
