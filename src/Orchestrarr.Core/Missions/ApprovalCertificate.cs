using System.Security.Cryptography;
using Orchestrarr.Core.SecondFactor;

namespace Orchestrarr.Core.Missions;

/// <summary>Whether a certificate is for one problem, or for a standing rule that covers a class of problems (FR-PLAN-4, FR-SAF-12).</summary>
public enum CertificateKind
{
    Problem = 0,
    Rule = 1,
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
        writer.Strings(Repositories);
        writer.Int((int)MaxWorld);
        writer.Int(Budget.MaxMinutes);
        writer.Int(Budget.MaxAttempts);
        writer.Int(Budget.MaxTurns);
        writer.Seconds(IssuedAt);
        writer.Seconds(ExpiresAt);
        writer.Text(Approver);
        writer.Text(Nonce);
        writer.Strings(ForbiddenPaths);
        writer.Int(MaxMandates);
        return writer.ToArray();
    }
}

public sealed record SignedApproval(ApprovalCertificate Certificate, byte[] Signature);

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
    private readonly IAuthorityLedger _ledger;
    private readonly object _gate = new();

    /// <summary>The authority takes ownership of <paramref name="key"/> when one is given: it disposes it.</summary>
    public ApprovalAuthority(SecondFactorGuard guard, TimeProvider time, Func<string> newId, ECDsa? key = null, ApprovalPolicy? policy = null, IAuthorityLedger? ledger = null)
    {
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(newId);
        _guard = guard;
        _time = time;
        _newId = newId;
        _key = key ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _policy = policy ?? ApprovalPolicy.Default;
        _ledger = ledger ?? new InMemoryAuthorityLedger();
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
        // Only the two kinds and the three worlds that exist can be certified: any other value would pass every check written for one of them
        if (!Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.MaxWorld))
        {
            return null;
        }
        var longest = request.Kind == CertificateKind.Rule ? _policy.MaxRuleLifetime : _policy.MaxProblemLifetime;
        // The floor of forbidden paths is part of every certificate, whatever the request lists, and the maintainer reads it in the summary
        var forbidden = (IReadOnlyList<string>)[.. (request.ForbiddenPaths ?? []).Union(_policy.ForbiddenPathsFloor, StringComparer.Ordinal)];
        request = request with { ForbiddenPaths = forbidden };
        // Every identifier is plain text on one line: nothing a requester writes can pass for a line, a separator or a quote of what the maintainer reads
        if (!OneRepository.Is(request.Repositories) || !request.Budget.IsValid || request.Lifetime <= TimeSpan.Zero || request.Lifetime > longest || request.MaxMandates < 1 || request.MaxMandates > _policy.MaxMandates
            || (request.Kind == CertificateKind.Rule && request.MaxWorld > _policy.MaxRuleWorld)
            || !Plain(request.Subject) || !Plain(request.ApplicationId) || !request.Repositories.All(Plain) || !forbidden.All(Plain))
        {
            return null;
        }
        var now = _time.GetUtcNow();
        var summary = Summary(request, forbidden);
        var digest = Convert.ToHexString(SHA256.HashData(CanonicalRequest(request, forbidden)));
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

    private static bool Plain(string value) => value.Length is > 0 and <= 200 && !value.Any(char.IsControl);

    // One labelled line per field, each value quoted: a repository is a line of its own, so ["a, b"] and ["a", "b"] cannot look alike
    private static string Summary(ApprovalRequest request, IReadOnlyList<string> forbidden)
    {
        static string Quote(string value) => System.Text.Json.JsonSerializer.Serialize(value);
        var lines = new List<string>
        {
            $"kind: {request.Kind}",
            $"subject: {Quote(request.Subject)}",
            $"application: {Quote(request.ApplicationId)}",
        };
        lines.AddRange(request.Repositories.Order(StringComparer.Ordinal).Select(repository => $"repository: {Quote(repository)}"));
        lines.Add($"highest world: {request.MaxWorld}");
        lines.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"budget: {request.Budget.MaxMinutes} minutes, {request.Budget.MaxAttempts} attempts, {request.Budget.MaxTurns} turns"));
        lines.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"mandates: at most {request.MaxMandates}"));
        lines.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"valid for: {request.Lifetime.TotalHours:0.##} hours"));
        lines.AddRange(forbidden.Order(StringComparer.Ordinal).Select(path => $"forbidden path: {Quote(path)}"));
        return string.Join('\n', lines);
    }

    // The digest is over the same canonical encoding as the certificate, never over the text that is displayed
    private static byte[] CanonicalRequest(ApprovalRequest request, IReadOnlyList<string> forbidden)
    {
        var writer = new CanonicalWriter();
        writer.Int((int)request.Kind);
        writer.Text(request.Subject);
        writer.Text(request.ApplicationId);
        writer.Strings(request.Repositories);
        writer.Int((int)request.MaxWorld);
        writer.Int(request.Budget.MaxMinutes);
        writer.Int(request.Budget.MaxAttempts);
        writer.Int(request.Budget.MaxTurns);
        writer.Int((int)request.Lifetime.TotalSeconds);
        writer.Strings(forbidden);
        writer.Int(request.MaxMandates);
        return writer.ToArray();
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
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approver);
        (ApprovalProposal Proposal, ApprovalRequest Request) held;
        lock (_gate)
        {
            if (!_proposals.Remove(proposalId, out held) || held.Proposal.ExpiresAt <= _time.GetUtcNow() || !string.Equals(held.Proposal.Approver, approver, StringComparison.Ordinal))
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

    /// <summary>The maintainer withdraws an approval before its expiry (a standing rule revoked): the runners and the control plane read it, and it is kept by the authority's ledger.</summary>
    public void Revoke(string certificateId) => _ledger.Revoke(certificateId);

    public bool IsRevoked(string certificateId) => _ledger.IsRevoked(certificateId);

    /// <summary>The certificates withdrawn so far.</summary>
    public IReadOnlyCollection<string> Revoked() => _ledger.Revoked();

    /// <summary>A runner asks, before it accepts a mandate, to count it against its certificate: one counter for every runner, held here.</summary>
    public MandateUse Use(string certificateId, string nonce, int maxMandates) => _ledger.Use(certificateId, nonce, maxMandates);

    public void Dispose() => _key.Dispose();
}
