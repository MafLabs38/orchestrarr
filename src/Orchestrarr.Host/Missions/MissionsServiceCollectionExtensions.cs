using System.Security.Cryptography;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Agents;
using Orchestrarr.Host.Storage;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Missions;

internal static class MissionsServiceCollectionExtensions
{
    /// <summary>The stores, the key that signs the mandates and the conductor of a problem (ADR 0030 to 0032), in the SQLite file or, in the sandbox, in memory.</summary>
    public static IServiceCollection AddMissions(this IServiceCollection services, IConfiguration configuration, IStorageSettings storage, string contentRoot)
    {
        var options = configuration.GetSection(MissionsOptions.SectionName).Get<MissionsOptions>() ?? new MissionsOptions();
        services.AddSingleton(options);
        var agents = configuration.GetSection(AgentsOptions.SectionName).Get<AgentsOptions>() ?? new AgentsOptions();
        // Refused here, at start, not on the first use of the controller
        var controllerTimeout = agents.ControllerTimeoutSeconds >= 10 ? TimeSpan.FromSeconds(agents.ControllerTimeoutSeconds) : throw new InvalidOperationException($"Agents:ControllerTimeoutSeconds must be at least 10 seconds, not {agents.ControllerTimeoutSeconds}.");
        services.AddSingleton(agents).AddSingleton<IAgentGateway>(provider => agents.Enabled ? new ClaudeCliGateway(agents, provider.GetRequiredService<ILogger<ClaudeCliGateway>>()) : new DisabledGateway())
            .AddSingleton(provider => new ControllerAgent(provider.GetRequiredService<IAgentGateway>(), agents.ControllerModel, controllerTimeout));
        if (storage is SqliteStorage)
        {
            services
                .AddSingleton<IMissionStore>(provider => new SqliteMissionStore(provider.GetRequiredService<SqliteDatabase>()))
                .AddSingleton<IMandateLedger>(provider => new SqliteMandateLedger(provider.GetRequiredService<SqliteDatabase>()))
                .AddSingleton<IProblemStore>(provider => new SqliteProblemStore(provider.GetRequiredService<SqliteDatabase>()))
                .AddSingleton<IDossierStore>(provider => new SqliteDossierStore(provider.GetRequiredService<SqliteDatabase>()));
        }
        else
        {
            services.AddSingleton<IMissionStore, InMemoryMissionStore>().AddSingleton<IMandateLedger, InMemoryMandateLedger>()
                .AddSingleton<IProblemStore, InMemoryProblemStore>().AddSingleton<IDossierStore, InMemoryDossierStore>();
        }
        return services
            .AddSingleton<ProblemLocks>()
            .AddSingleton(_ => SigningKey.Open(options.SigningKeyFile ?? (storage is SqliteStorage file ? Path.Combine(Path.GetDirectoryName(file.Path) ?? contentRoot, "keys", "mandate-signing.key") : null)))
            .AddSingleton<IRunnerDirectory>(new ConfiguredRunners(options))
            .AddSingleton<IFactsSource, ConservativeFacts>()
            .AddSingleton<IApprovalSource, NoApprovals>()
            .AddSingleton<IGrantSource>(new ConfiguredGrants(configuration))
            .AddSingleton(provider => AuthorityKey.Verifier(options.ApprovalAuthorityPublicKeyFile, provider.GetRequiredService<TimeProvider>()))
            .AddSingleton<IStopSignal>(provider => new KillSwitchSignal(provider.GetRequiredService<IKillSwitchControl>()))
            .AddSingleton<IMissionAudit>(provider => new AuditTrailMissions(provider.GetRequiredService<IAuditSink>(), provider.GetRequiredService<TimeProvider>()))
            .AddHostedService<MissionSweeper>()
            .AddSingleton<DemoRun>()
            .AddHostedService<MissionsNotWiredNotice>()
            .AddSingleton<IApprovalWithdrawals, UnreadableWithdrawals>()
            .AddSingleton(provider => new MissionStores(provider.GetRequiredService<IMissionStore>(), provider.GetRequiredService<IMandateLedger>(), provider.GetRequiredService<IProblemStore>(), provider.GetRequiredService<IDossierStore>()))
            .AddSingleton(provider => new PowerSources(provider.GetRequiredService<IApprovalSource>(), provider.GetRequiredService<ApprovalVerifier>(), provider.GetRequiredService<IApprovalWithdrawals>(), provider.GetRequiredService<IGrantSource>(), provider.GetRequiredService<IStopSignal>()))
            .AddSingleton(provider => new ConductorServices(provider.GetRequiredService<IFactsSource>(), provider.GetRequiredService<IRunnerDirectory>(), provider.GetRequiredService<IMissionAudit>(), provider.GetRequiredService<TimeProvider>(), () => Guid.NewGuid().ToString("N")))
            .AddSingleton(provider => new ProblemConductor(
                provider.GetRequiredService<MandateSigner>(), provider.GetRequiredService<MissionStores>(), provider.GetRequiredService<PowerSources>(), provider.GetRequiredService<ConductorServices>(), options.Conductor(), provider.GetRequiredService<ProblemLocks>()));
    }
}

/// <summary>Opens the signing key of the control plane: kept in a file for its owner only when there is a place, else made for this run.</summary>
internal static class SigningKey
{
    public static MandateSigner Open(string? file)
    {
        if (file is null)
        {
            return MandateSigner.Create();
        }
        if (File.Exists(file))
        {
            return MandateSigner.FromPrivateKey(File.ReadAllBytes(file));
        }
        var signer = MandateSigner.Create();
        var folder = Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".";
        Directory.CreateDirectory(folder);
        // Written whole under a name of its own, for its owner only from the first byte, then moved into place without replacing: a start that comes second, or one over a half-written file, never reads a truncated key
        var pending = Path.Combine(folder, $".{Path.GetFileName(file)}.{Guid.NewGuid():N}.pending");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(pending, options))
        {
            stream.Write(signer.PrivateKey());
        }
        try
        {
            File.Move(pending, file, overwrite: false);
            return signer;
        }
        catch (IOException)
        {
            // Another start put its key there first: the one in place is the key of the control plane
            File.Delete(pending);
            signer.Dispose();
            return MandateSigner.FromPrivateKey(File.ReadAllBytes(file));
        }
    }
}

internal sealed class ConfiguredRunners(MissionsOptions options) : IRunnerDirectory
{
    public string RunnerFor(World world, string applicationId) => world switch
    {
        World.Staging => options.StagingRunner,
        World.Git => options.GitRunner,
        World.Release => options.ReleaseRunner,
        _ => throw new ArgumentOutOfRangeException(nameof(world), world, "a world that does not exist has no runner"),
    };
}

/// <summary>
/// Until the control plane can fetch the real facts (the diff and the CI status from the forge on the exact commit, the health from Portarr), it states only what it knows by construction (the plan
/// covers the problem, because the problem was started from it). **A fact it does not know counts as failing**: the diff is not known to be clean, the budget accounting is not built, the pipelines and
/// the artifact are unknown, the claim is not checked. So no mandate for another world is granted on this source: it is for the demo and the tests to bring their own.
/// </summary>
internal sealed class ConservativeFacts : IFactsSource
{
    public ValueTask<ObservedFacts> FactsAsync(FactsQuery query, CancellationToken cancellationToken) => ValueTask.FromResult(new ObservedFacts(
        BudgetAvailable: false, DiffIsClean: null, CiGreenOnExactCommit: null, ArtifactIsTheOneProvedInStaging: null, WayBackKnown: null, WorkerClaimMatchesFacts: false));
}

/// <summary>The list of the certificates that the authority withdrew cannot be read yet (the authority is not deployed): **every certificate counts as withdrawn**, the same direction as a stop that cannot be read.</summary>
internal sealed class UnreadableWithdrawals : IApprovalWithdrawals
{
    public bool IsWithdrawn(string certificateId) => true;
}

/// <summary>The maintainer's approval is proved by a certificate of the approval authority (ADR 0031 point 7). The plan service that links a plan entry to a certificate is not built: until then none is given, so no real mandate is issued.</summary>
internal sealed class NoApprovals : IApprovalSource
{
    public SignedApproval? For(PlanEntry entry) => null;
}

/// <summary>The grants the maintainer wrote in the settings, read from the configuration at every call (it reloads when the file changes): a grant removed from the settings is gone at the next crossing.</summary>
internal sealed class ConfiguredGrants(IConfiguration configuration) : IGrantSource
{
    // An application id is compared as it is written (ordinal), and one that holds the path separator of the configuration cannot name another section
    public World? MaxWorldFor(string applicationId)
    {
        if (applicationId.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }
        var grant = configuration.GetSection($"{MissionsOptions.SectionName}:Grants").GetChildren().FirstOrDefault(child => string.Equals(child.Key, applicationId, StringComparison.Ordinal));
        return grant?.Value is { } name && Enum.TryParse<World>(name, ignoreCase: true, out var world) && Enum.IsDefined(world) ? world : null;
    }
}

internal static class AuthorityKey
{
    /// <summary>The verifier of the authority's certificates, from its public key. Without a key, a verifier of a key nobody holds: no certificate is valid.</summary>
    public static ApprovalVerifier Verifier(string? file, TimeProvider time)
    {
        if (file is { Length: > 0 })
        {
            // A path that is set and leads nowhere is a mistake to stop on, not the same as no key (which is the safe state: nothing is valid)
            return File.Exists(file) ? new ApprovalVerifier(File.ReadAllBytes(file), time) : throw new FileNotFoundException("Missions:ApprovalAuthorityPublicKeyFile names a file that does not exist.", file);
        }
        using var nobody = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new ApprovalVerifier(nobody.ExportSubjectPublicKeyInfo(), time);
    }
}

internal sealed class KillSwitchSignal(IKillSwitchControl killSwitch) : IStopSignal
{
    public bool Engaged => killSwitch.Engaged;
}

/// <summary>Writes the events of the missions to the audit trail, in order, with the others (ADR 0013).</summary>
internal sealed class AuditTrailMissions(IAuditSink sink, TimeProvider time) : IMissionAudit
{
    public ValueTask<bool> RecordAsync(string eventName, string subject, string? detail, CancellationToken cancellationToken) =>
        sink.TryRecordAsync(new AuditEntry(time.GetUtcNow(), eventName, subject.Length > 120 ? subject[..120] : subject, null, detail is { Length: > 400 } ? detail[..400] : detail), cancellationToken);
}

/// <summary>Says once, at start, which sources of the missions are not wired yet, so that "no mandate is ever issued" is not mistaken for a bug: every act is refused until they exist (ADR 0031 point 7).</summary>
internal sealed class MissionsNotWiredNotice(IFactsSource facts, IApprovalSource approvals, IApprovalWithdrawals withdrawals, ILogger<MissionsNotWiredNotice> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var missing = new[]
        {
            facts is ConservativeFacts ? "the source of the facts (forge, Portarr)" : null,
            approvals is NoApprovals ? "the plan service that gives an approval" : null,
            withdrawals is UnreadableWithdrawals ? "the list of the withdrawn certificates" : null,
        }.OfType<string>().ToList();
        if (missing.Count > 0)
        {
            logger.LogWarning("The missions are inert: {Missing} not wired yet, so every act is refused and no real mandate can be issued", string.Join(", ", missing));
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Ends what no runner will say for itself, on a timer: the global stop engaged, a mandate that expired without a runner, a problem past its time bound. It starts nothing, so the stop does not hold it back.
/// </summary>
internal sealed class MissionSweeper(ProblemConductor conductor, ILogger<MissionSweeper> logger, TimeProvider time) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    _ = await conductor.SweepAsync(stoppingToken);
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    logger.LogWarning(failure, "The sweep of the missions failed; the next pass will try again");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping
        }
    }
}
