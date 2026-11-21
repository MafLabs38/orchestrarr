using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;
using Orchestrarr.Core.SecondFactor;
using Orchestrarr.Providers.GitHub;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// **Sandbox only.** The approval authority of the test bed, in this process: the maintainer's validation of a problem is made into a certificate that the conductor and the runners check, with the account of the
/// sandbox and the code computed here (nothing to protect in the test bed). In a deployment the authority is a service apart with its own channel (ADR 0031 point 7).
/// </summary>
internal sealed class SandboxApprovals : IApprovalSource
{
    private const string Subject = "sandbox";
    private readonly ApprovalAuthority _authority;
    private readonly SandboxSecrets _secrets;
    private readonly SecondFactorOptions _factor = new();
    private readonly TimeProvider _time;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SignedApproval> _prepared = new(StringComparer.Ordinal);

    public SandboxApprovals(TimeProvider time)
    {
        _time = time;
        _secrets = new SandboxSecrets(OtpAuthUri.NewSecret());
        _authority = new ApprovalAuthority(new SecondFactorGuard(_secrets, new SandboxUseLedger(), time, _factor), time, () => Guid.NewGuid().ToString("N"));
        Verifier = new ApprovalVerifier(_authority.PublicKey(), time);
    }

    public ApprovalVerifier Verifier { get; }

    /// <summary>Asks the authority for the certificate of this problem (the problem, one repository, the highest world, the budget, two hours) and keeps it for the conductor, which reads it under its lock.</summary>
    public async Task<bool> PrepareAsync(PlanEntry entry, CancellationToken cancellationToken)
    {
        var secret = await _secrets.FindAsync(Subject, cancellationToken);
        if (secret is null)
        {
            return false;
        }
        var code = Totp.Code(secret, Totp.StepAt(_time.GetUtcNow(), _factor.Totp), _factor.Totp);
        var request = new ApprovalRequest(CertificateKind.Problem, entry.ProblemId, entry.ApplicationId, entry.Repositories, entry.MaxWorld, entry.Budget, TimeSpan.FromHours(2));
        if (_authority.Propose(request, Subject) is { } proposal && (await _authority.ApproveAsync(proposal.Id, Subject, code, cancellationToken)).Approval is { } approval)
        {
            _prepared[entry.ProblemId] = approval;
            return true;
        }
        return false;
    }

    public SignedApproval? For(PlanEntry entry) => _prepared.GetValueOrDefault(entry.ProblemId);

    /// <summary>The code is computed here and typed by nobody: a step that was used is not a reason to refuse the next row of the same selection (nothing to protect in the test bed; the real authority has to approve a batch with one code).</summary>
    private sealed class SandboxUseLedger : ISecondFactorUseLedger
    {
        public ValueTask<bool> TryAdvanceAsync(string subject, long step, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class SandboxSecrets(byte[] secret) : ISecondFactorSecrets
    {
        public ValueTask<byte[]?> FindAsync(string subject, CancellationToken cancellationToken) => ValueTask.FromResult<byte[]?>(subject == Subject ? secret : null);
    }
}

internal sealed class SandboxWithdrawals : IApprovalWithdrawals
{
    public bool IsWithdrawn(string certificateId) => false;
}

/// <summary>
/// **Sandbox only.** The facts of a problem that is a pull request, read from GitHub by the control plane itself: the head and base commits, the checks of the head, the paths that change. What a source of the facts
/// of a deployment would read from the rulesets and the workflows (merging does not deploy, no tag, no credential in reach of the agent) is **declared here to hold**, because the test bed has no agent identity to
/// confine; it is said in the log at start (`SandboxNotice`).
/// </summary>
internal sealed class SandboxFacts(IProblemStore problems, IPullRequestFacts pulls, IMissionStore missions, IDossierStore dossiers, ISandboxHealth? health = null) : IFactsSource
{
    private static readonly TimeSpan ForgeDeadline = TimeSpan.FromSeconds(20);

    // What was proved in staging is the result of the merge (the tree), and the release is the same tree: a merge made on a base that moved since is another artifact. Unknown until the pull request is merged.
    private bool? ArtifactProvedInStaging(string problemId, PullRequestFacts read)
    {
        if (!read.Merged || read.MergeTree is null)
        {
            return null;
        }
        var proved = missions.ForProblem(problemId).Where(mission => mission.World == World.Staging).Select(mission => dossiers.Get(mission.Id))
            .LastOrDefault(dossier => dossier is not null)?.EvidenceDigests.FirstOrDefault(digest => digest.StartsWith(ArtifactEvidence, StringComparison.Ordinal));
        return proved is not null && string.Equals(proved[ArtifactEvidence.Length..], read.MergeTree, StringComparison.Ordinal);
    }

    /// <summary>The evidence of the staging proof that names the tree it built and tested (the runner of missions writes it, the facts source compares it).</summary>
    public const string ArtifactEvidence = "artifact:tree=";

    public async ValueTask<ObservedFacts> FactsAsync(FactsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entry = problems.Get(query.ProblemId)?.Entry;
        // The forge is read within a time of its own: this is called while the lock of the problem is held, and a slow forge must not hold its stop
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ForgeDeadline);
        PullRequestFacts? read = null;
        if (entry is { PullRequest: { } number } && entry.Repositories.Count == 1)
        {
            try
            {
                read = await pulls.ReadAsync(entry.Repositories[0], number, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The forge did not answer in time: the facts are unknown, which fails the checks
            }
        }
        if (entry is null || read is null)
        {
            return new ObservedFacts(BudgetAvailable: false, DiffIsClean: null, CiGreenOnExactCommit: null, ArtifactIsTheOneProvedInStaging: null, WayBackKnown: null, WorkerClaimMatchesFacts: false);
        }
        // Once the pull request is merged, what runs is the commit that the merge made on the base branch: the commit, the checks and the tree that the later worlds are about
        var commit = read.Merged && read.MergeCommit is { } merged ? merged : read.HeadSha;
        var checks = read.Merged ? read.MergeChecks : read.Checks;
        return new ObservedFacts(
            BudgetAvailable: true,
            DiffIsClean: read.PathsComplete,
            CiGreenOnExactCommit: checks switch { ChecksState.Passing => true, ChecksState.Failing => false, _ => null },
            ArtifactIsTheOneProvedInStaging: ArtifactProvedInStaging(query.ProblemId, read),
            WayBackKnown: true,
            WorkerClaimMatchesFacts: query.Dossier?.Commit is { } claimed && string.Equals(claimed, commit, StringComparison.Ordinal),
            MergeMayDeploy: false,
            ExecutedFilesTouched: read.ChangedPaths.Any(path => path.StartsWith(".github/", StringComparison.Ordinal)),
            TagCreationBlocked: true,
            BranchCannotReachWriteCredentials: true,
            HeadCommit: commit,
            IdentityConfinedToItsBranches: true,
            ChangedPaths: read.ChangedPaths,
            Repository: entry.Repositories[0],
            BaseCommit: read.BaseSha,
            Health: query.World == World.Release && health is not null ? await health.ReadAsync(entry.Repositories[0], cancellationToken) : null);
    }
}

/// <summary>The health of a deployed application, read by the control plane itself (never the worker's word): a sentence for the controller, or null when it is not read.</summary>
internal interface ISandboxHealth
{
    Task<string?> ReadAsync(string repository, CancellationToken cancellationToken);
}

internal sealed class HttpSandboxHealth(string host, int basePort) : ISandboxHealth
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };

    public async Task<string?> ReadAsync(string repository, CancellationToken cancellationToken)
    {
        // The applications of the test bed serve plain HTTP on the lab network and carry nothing secret: the probe reads a health route only
#pragma warning disable S5332
        var address = new Uri($"http://{host}:{DeploymentPort.Of(repository, basePort)}/health");
#pragma warning restore S5332
        try
        {
            using var response = await Client.GetAsync(address, cancellationToken);
            return $"GET {address} answered {(int)response.StatusCode}, read by the control plane";
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
        {
            return $"GET {address} did not answer, read by the control plane";
        }
    }
}

/// <summary>The standing grant of the test bed: every application may go up to one world, set by <c>Missions:SandboxMaxWorld</c> (the last world: the test bed merges and deploys).</summary>
internal sealed class SandboxGrants(World highest) : IGrantSource
{
    public World? MaxWorldFor(string applicationId) => highest;
}

/// <summary>Says at start, once, what the test bed declares to hold instead of reading it, so that nobody takes the sandbox for a deployment.</summary>
internal sealed class SandboxNotice(World highest, ILogger<SandboxNotice> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning("Missions:Sandbox is on: the approvals are made by an authority in this process with no code to type, every application is granted up to {World}, the act gateway MERGES pull requests with the GitHub token of this host, and the frame is DECLARED to hold (merging does not deploy, no tag can be created, no credential is in reach of the branch, the identity is confined to its branches). A deployment never sets it.", highest);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class SandboxMissionsExtensions
{
    /// <summary>Replaces the sources that fail closed by the ones of the test bed. Only where the host says that it is a development host.</summary>
    public static IServiceCollection AddSandboxMissions(this IServiceCollection services, IConfiguration configuration, bool isDevelopment)
    {
        if (!isDevelopment)
        {
            throw new InvalidOperationException("Missions:Sandbox is for a development host only: it declares the frame to hold and approves with an account of its own.");
        }
        var github = configuration.GetSection(GitHubOptions.SectionName).Get<GitHubOptions>() ?? new GitHubOptions();
        var missions = configuration.GetSection(MissionsOptions.SectionName).Get<MissionsOptions>() ?? new MissionsOptions();
        if (missions.SandboxDeployHost is { Length: > 0 } deployHost)
        {
            services.AddSingleton<ISandboxHealth>(new HttpSandboxHealth(deployHost, missions.SandboxDeployPortBase));
        }
        return services
            .AddSingleton<SandboxApprovals>()
            .AddSingleton<IApprovalSource>(provider => provider.GetRequiredService<SandboxApprovals>())
            .AddSingleton(provider => provider.GetRequiredService<SandboxApprovals>().Verifier)
            .AddSingleton<IApprovalWithdrawals, SandboxWithdrawals>()
            .AddSingleton<IGrantSource>(new SandboxGrants(missions.SandboxMaxWorld))
            .AddHostedService(provider => new SandboxNotice(missions.SandboxMaxWorld, provider.GetRequiredService<ILogger<SandboxNotice>>()))
            .AddSingleton<IPullRequestFacts>(_ => string.IsNullOrWhiteSpace(github.Token) ? new NoPullRequestFacts() : GitHubPullRequestFacts.Create(github))
            .AddSingleton<IForgeActs>(_ => string.IsNullOrWhiteSpace(github.Token) ? new NoForgeActs() : GitHubForgeActs.Create(github))
            .AddSingleton<IFactsSource, SandboxFacts>();
    }
}
