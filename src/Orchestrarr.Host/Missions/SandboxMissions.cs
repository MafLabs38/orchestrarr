using Orchestrarr.Core.Fleet;
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
        var request = new ApprovalRequest(CertificateKind.Problem, entry.ProblemId, entry.ApplicationId, entry.Repositories, entry.MaxWorld, entry.Budget, TimeSpan.FromHours(2), PipelineChanges.ForbiddenFor(entry));
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
internal sealed class SandboxFacts(IProblemStore problems, IPullRequestFacts pulls, IMissionStore missions, IDossierStore dossiers, ISandboxHealth? health = null, RepairRegistry? repairs = null) : IFactsSource
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
            .LastOrDefault(dossier => dossier is not null)?.EvidenceDigests.FirstOrDefault(digest => digest.StartsWith(MissionEvidence.ArtifactTree, StringComparison.Ordinal));
        return proved is not null && string.Equals(proved[MissionEvidence.ArtifactTree.Length..], read.MergeTree, StringComparison.Ordinal);
    }

    public async ValueTask<ObservedFacts> FactsAsync(FactsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entry = problems.Get(query.ProblemId)?.Entry;
        // The forge is read within a time of its own: this is called while the lock of the problem is held, and a slow forge must not hold its stop
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ForgeDeadline);
        PullRequestFacts? read = null;
        // The pull request that the problem is about: the one that carries its repair once there is one, else the one it started from
        (int Number, string Head)? current = null;
        if (entry is not null)
        {
            current = repairs?.Effective(query.ProblemId, entry);
            if (current is null && entry is { PullRequest: { } started, HeadSha: { } startedHead })
            {
                current = (started, startedHead);
            }
        }
        // A problem that is not a pull request has no pull request to read until the gateway opens the one that carries its change: what is known then is the branch that the agent finished, as the control plane read it on the forge
        if (entry is { PullRequest: null } && current is null && entry.Repositories.Count == 1 && repairs?.PendingOf(query.ProblemId) is { Branch: { } branch } change)
        {
            return new ObservedFacts(
                BudgetAvailable: true,
                // The policy was applied by the control plane to what the forge says the branch holds, before the branch was kept
                DiffIsClean: true,
                // No pipeline has run on a change that is not in a pull request yet: at the crossing into the Git world it is declared green (as a repair that waits is: the pipeline of the pull request that will carry it is what the merge
                // is judged on), and at an act it is not known, which the act of opening a pull request accepts and nothing else does
                CiGreenOnExactCommit: query.Repository is null ? true : null,
                ArtifactIsTheOneProvedInStaging: null,
                WayBackKnown: true,
                // The dossier names the commit that the agent finished at, and that is the commit that the control plane audited
                WorkerClaimMatchesFacts: query.Dossier?.Commit is { } stated && string.Equals(stated, branch.Sha, StringComparison.Ordinal),
                MergeMayDeploy: false,
                ExecutedFilesTouched: branch.Paths.Any(path => path.StartsWith(".github/", StringComparison.Ordinal)),
                TagCreationBlocked: true,
                BranchCannotReachWriteCredentials: true,
                HeadCommit: branch.Sha,
                IdentityConfinedToItsBranches: true,
                ChangedPaths: branch.Paths,
                Repository: entry.Repositories[0],
                BaseCommit: change.OriginalHead,
                ChangeReading: branch.Reading);
        }
        if (entry is not null && current is { } target && entry.Repositories.Count == 1)
        {
            try
            {
                read = await pulls.ReadAsync(entry.Repositories[0], target.Number, deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The forge did not answer in time: the facts are unknown, which fails the checks
            }
        }
        // Staging proves the merge onto the default branch: a pull request that targets another base is not one that this source can speak for (unknown counts as failing)
        if (entry is null || read is null || !read.TargetsDefaultBranch)
        {
            // A problem with no pull request and no change handed over has nothing on the forge to read: that is not a budget that ran out (the other facts are unknown, which fails their checks)
            return new ObservedFacts(BudgetAvailable: entry is { PullRequest: null }, DiffIsClean: null, CiGreenOnExactCommit: null, ArtifactIsTheOneProvedInStaging: null, WayBackKnown: null, WorkerClaimMatchesFacts: false);
        }
        // Once the pull request is merged, what runs is the commit that the merge made on the base branch: the commit, the checks and the tree that the later worlds are about
        var commit = read.Merged && read.MergeCommit is { } merged ? merged : read.HeadSha;
        var checks = read.Merged ? read.MergeChecks : read.Checks;
        // Only at the crossing into the Git world (no repository is named): an act (the merge) is judged on the pipeline of the pull request that it is about, never on this declaration
        var repairWaiting = query.Repository is null && repairs?.PendingOf(query.ProblemId) is not null && repairs.FixOf(query.ProblemId) is null;
        return new ObservedFacts(
            BudgetAvailable: true,
            DiffIsClean: read.PathsComplete,
            // A repair that is not in a pull request yet has no commit for a pipeline to have run on: the pipeline of the pull request that will carry it is what the merge checks, and what the staging proved is what the release checks
            CiGreenOnExactCommit: repairWaiting ? true : checks switch { ChecksState.Passing => true, ChecksState.Failing => false, _ => null },
            ArtifactIsTheOneProvedInStaging: ArtifactProvedInStaging(query.ProblemId, read),
            WayBackKnown: true,
            // The dossier of the staging world names the commit that the repair started from, which is not the head of the pull request that carries the repair
            WorkerClaimMatchesFacts: query.Dossier?.Commit is { } claimed && (string.Equals(claimed, commit, StringComparison.Ordinal) || string.Equals(claimed, repairs?.FixOf(query.ProblemId)?.OriginalHead, StringComparison.Ordinal)),
            MergeMayDeploy: false,
            ExecutedFilesTouched: read.ChangedPaths.Any(path => path.StartsWith(".github/", StringComparison.Ordinal)),
            TagCreationBlocked: true,
            BranchCannotReachWriteCredentials: true,
            HeadCommit: commit,
            IdentityConfinedToItsBranches: true,
            ChangedPaths: read.ChangedPaths,
            Repository: entry.Repositories[0],
            BaseCommit: read.BaseSha,
            Merged: read.Merged,
            BaseWasRedBeforeMerge: read.Merged ? read.BaseWasRed : null,
            Health: query.World == World.Release && health is not null ? await health.ReadAsync(entry.Repositories[0], cancellationToken) : null);
    }
}

/// <summary>The push credentials of the test bed are made for its repositories only (<c>Missions:SandboxMergeRepositories</c>), whatever the App is installed on.</summary>
internal sealed class GuardedPushCredentials(IPushCredentials inner, IReadOnlyList<string> allowed) : IPushCredentials
{
    public Task<PushCredential?> IssueAsync(string repositoryFullName, bool pipelines, CancellationToken cancellationToken) =>
        allowed.Any(pattern => Orchestrarr.Core.Tasks.RepositoryPattern.Matches(pattern, repositoryFullName)) ? inner.IssueAsync(repositoryFullName, pipelines, cancellationToken) : Task.FromResult<PushCredential?>(null);

    public Task<bool> RevokeAsync(string token, CancellationToken cancellationToken) => inner.RevokeAsync(token, cancellationToken);
}

/// <summary>What the gateway writes (a merge, a branch and a pull request, the closing of a pull request) is done on the repositories that the settings name and no other: the gateway does not write where it was not told it may, whatever a row says.</summary>
internal sealed class GuardedForgeActs(IForgeActs inner, IReadOnlyList<string> allowed) : IForgeActs
{
    public Task<MergeResult> MergePullRequestAsync(string repositoryFullName, int number, string expectedHeadSha, CancellationToken cancellationToken) =>
        Allowed(repositoryFullName)
            ? inner.MergePullRequestAsync(repositoryFullName, number, expectedHeadSha, cancellationToken)
            : Task.FromResult(new MergeResult(false, null, "the gateway may not merge on this repository (Missions:SandboxMergeRepositories)"));

    public Task<OpenedPullRequest> OpenPullRequestAsync(string repositoryFullName, string baseBranch, string parentSha, string branch, IReadOnlyList<PatchedFile> files, string title, string body, CancellationToken cancellationToken) =>
        Allowed(repositoryFullName)
            ? inner.OpenPullRequestAsync(repositoryFullName, baseBranch, parentSha, branch, files, title, body, cancellationToken)
            : Task.FromResult(new OpenedPullRequest(false, 0, null, "the gateway may not write on this repository (Missions:SandboxMergeRepositories)"));

    public Task<bool> ClosePullRequestAsync(string repositoryFullName, int number, string comment, CancellationToken cancellationToken) =>
        Allowed(repositoryFullName) ? inner.ClosePullRequestAsync(repositoryFullName, number, comment, cancellationToken) : Task.FromResult(false);

    public Task<OpenedPullRequest> OpenPullRequestFromBranchAsync(string repositoryFullName, string baseBranch, string branch, string expectedSha, string title, string body, CancellationToken cancellationToken) =>
        Allowed(repositoryFullName)
            ? inner.OpenPullRequestFromBranchAsync(repositoryFullName, baseBranch, branch, expectedSha, title, body, cancellationToken)
            : Task.FromResult(new OpenedPullRequest(false, 0, null, "the gateway may not write on this repository (Missions:SandboxMergeRepositories)"));

    public Task<bool> RequestBotRebaseAsync(string repositoryFullName, int number, string command, CancellationToken cancellationToken) =>
        Allowed(repositoryFullName) ? inner.RequestBotRebaseAsync(repositoryFullName, number, command, cancellationToken) : Task.FromResult(false);

    private bool Allowed(string repository) => allowed.Any(pattern => Orchestrarr.Core.Tasks.RepositoryPattern.Matches(pattern, repository));
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
internal sealed class SandboxGrants(World highest, IReadOnlyDictionary<string, World>? limits = null) : IGrantSource
{
    // An application with a limit of its own is granted the lower of the two
    public World? MaxWorldFor(string applicationId) => limits is not null && limits.TryGetValue(applicationId, out var limit) && limit < highest ? limit : highest;
}

/// <summary>Says at start, once, what the test bed declares to hold instead of reading it, so that nobody takes the sandbox for a deployment.</summary>
internal sealed class SandboxNotice(World highest, ILogger<SandboxNotice> logger, IReadOnlyDictionary<string, World>? limits = null) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning("Missions:Sandbox is on: the approvals are made by an authority in this process with no code to type, every application is granted up to {World}{Limits}, the act gateway MERGES pull requests on the repositories of Missions:SandboxMergeRepositories with Missions:SandboxMergeToken (nothing is merged without both), and the frame is DECLARED to hold, as is the way back (the previous version is read by the runner when it deploys, after the authorisation) (merging does not deploy, no tag can be created, no credential is in reach of the branch, the identity is confined to its branches). A deployment never sets it.", highest, limits is { Count: > 0 } ? $" (lowered for {string.Join(", ", limits.Select(limit => $"{limit.Key}: {limit.Value}"))})" : string.Empty);
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
            if (Uri.CheckHostName(deployHost) == UriHostNameType.Unknown)
            {
                throw new InvalidOperationException("Missions:SandboxDeployHost must be a host name or an address.");
            }
            if (!DeploymentPort.IsValidBase(missions.SandboxDeployPortBase))
            {
                throw new InvalidOperationException("Missions:SandboxDeployPortBase must leave room for 100 ports below 65535.");
            }
            services.AddSingleton<ISandboxHealth>(new HttpSandboxHealth(deployHost, missions.SandboxDeployPortBase));
        }
        // One reader of the forge for the facts of a pull request and for the default branch, made when it is first needed
        var githubFacts = new Lazy<GitHubPullRequestFacts?>(() => string.IsNullOrWhiteSpace(github.Token) ? null : GitHubPullRequestFacts.Create(github));
        // The fake source of findings reaches the rows: the lab can plant an advisory that no real scanner reports (ADR 0055)
        if (!string.IsNullOrWhiteSpace(missions.SandboxFindingsFile))
        {
            // A file that is set and cannot be read in is said at start, never ignored: the demo would run without the faults it plants
            if (services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IFleetSource)) is not { ImplementationFactory: { } real } fleet)
            {
                throw new InvalidOperationException("Missions:SandboxFindingsFile is set but the source of the fleet is not registered by a factory that it can wrap.");
            }
            var file = missions.SandboxFindingsFile;
            services.Remove(fleet);
            services.AddSingleton<IFleetSource>(provider => new SandboxFleetFindings((IFleetSource)real(provider), file));
        }
        return services
            .AddSingleton<SandboxApprovals>()
            .AddSingleton<RepairRegistry>()
            .AddSingleton<BotPullRequests>()
            .AddSingleton<ProblemStarter>()
            .AddSingleton<IApprovalSource>(provider => provider.GetRequiredService<SandboxApprovals>())
            .AddSingleton(provider => provider.GetRequiredService<SandboxApprovals>().Verifier)
            .AddSingleton<IApprovalWithdrawals, SandboxWithdrawals>()
            .AddSingleton<IGrantSource>(new SandboxGrants(missions.SandboxMaxWorld, missions.SandboxWorldLimits))
            .AddHostedService(provider => new SandboxNotice(missions.SandboxMaxWorld, provider.GetRequiredService<ILogger<SandboxNotice>>(), missions.SandboxWorldLimits))
            .AddSingleton<IPullRequestFacts>(_ => githubFacts.Value is { } facts ? facts : new NoPullRequestFacts())
            .AddSingleton<IDefaultBranchSource>(_ => githubFacts.Value is { } facts ? facts : new NoDefaultBranchSource())
            .AddSingleton<IBranchSource>(_ => githubFacts.Value is { } facts ? facts : new NoBranchSource())
            .AddSingleton<IPipelineFailures>(_ => githubFacts.Value is { } facts ? facts : new NoPipelineFailures())
            .AddSingleton<IForgeActs>(_ => string.IsNullOrWhiteSpace(missions.SandboxMergeToken) ? new NoForgeActs() : new GuardedForgeActs(GitHubForgeActs.Create(github.WithToken(missions.SandboxMergeToken)), missions.SandboxMergeRepositories))
            .AddSingleton<IPushCredentials>(provider => string.IsNullOrWhiteSpace(missions.SandboxAgentAppClientId) || string.IsNullOrWhiteSpace(missions.SandboxAgentAppKeyFile)
                ? new NoPushCredentials()
                : new GuardedPushCredentials(GitHubAppPushCredentials.Create(github, missions.SandboxAgentAppClientId, File.ReadAllText(missions.SandboxAgentAppKeyFile),
                    said: refusal => provider.GetRequiredService<ILoggerFactory>().CreateLogger("Orchestrarr.Missions.PushCredentials").LogWarning("{Refusal}", refusal)), missions.SandboxMergeRepositories))
            .AddSingleton<PushCredentialLedger>()
            .AddSingleton<IFactsSource, SandboxFacts>()
            // What the lab planted for a repository wins (its fake advisories, the grain of sand); else what the forge says of the alerts of the repository
            .AddSingleton<IDependencyFindings>(_ => new FirstDependencyFindings([
                string.IsNullOrWhiteSpace(missions.SandboxFindingsFile) ? new NoDependencyFindings() : new FileDependencyFindings(missions.SandboxFindingsFile),
                githubFacts.Value is { } facts ? facts : new NoDependencyFindings()]));
    }
}
