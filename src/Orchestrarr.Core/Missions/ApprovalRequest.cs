namespace Orchestrarr.Core.Missions;

/// <summary>What the maintainer asks the authority to certify. <c>MaxMandates</c> is how many mandates the whole certificate may back, counted by the runners: a certificate for a standing rule would otherwise back any number of problems.</summary>
public sealed record ApprovalRequest(CertificateKind Kind, string Subject, string ApplicationId, IReadOnlyList<string> Repositories, World MaxWorld, MissionBudget Budget, TimeSpan Lifetime, IReadOnlyList<string>? ForbiddenPaths = null, int MaxMandates = 8);

/// <summary>The bounds that the authority puts on what it signs, whoever asks (and a **floor of forbidden paths** that is in every certificate, so that a request that lists none still protects the code owners, the bot's configuration and the secrets; the pipelines of an operation that is not about them are added by the request itself, ADR 0055): a certificate lives for a limited time and backs a limited number of mandates (a standing rule lives longer than a problem and may reach the release world, bounded by its class, its count and its expiry, **once FR-MAN-1c binds each of its mandates to its finding and the authority has its own channel**: until then the default caps it at the Git world, and only a change of this default lifts the cap, ADR 0031 point 7 and D-94).</summary>
public sealed record ApprovalPolicy(TimeSpan MaxProblemLifetime, TimeSpan MaxRuleLifetime, int MaxMandates, TimeSpan ProposalLifetime, World MaxRuleWorld, IReadOnlyList<string> ForbiddenPathsFloor)
{
    public static ApprovalPolicy Default { get; } = new(TimeSpan.FromDays(7), TimeSpan.FromDays(30), 50, TimeSpan.FromMinutes(10), World.Git, ForbiddenPathsDefaults.Floor);
}

/// <summary>
/// A request the authority holds until the maintainer confirms it **on the authority's own channel**: <c>Summary</c> is what the authority shows and <c>Digest</c> identifies it. The code that
/// confirms it signs exactly this and nothing else, so whoever relays the code cannot choose what is signed.
/// </summary>
public sealed record ApprovalProposal(string Id, string Approver, string Summary, string Digest, DateTimeOffset ExpiresAt);
