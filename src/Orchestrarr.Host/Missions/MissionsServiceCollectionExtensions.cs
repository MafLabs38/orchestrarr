using System.Security.Cryptography;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Host.Agents;
using Orchestrarr.Core.Missions;
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
        services.AddSingleton(agents).AddSingleton<IAgentGateway>(agents.Enabled ? new ClaudeCliGateway(agents) : new DisabledGateway())
            .AddSingleton(provider => new ControllerAgent(provider.GetRequiredService<IAgentGateway>(), agents.ControllerModel, TimeSpan.FromSeconds(Math.Max(10, agents.ControllerTimeoutSeconds))));
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
            .AddSingleton(_ => SigningKey.Open(options.SigningKeyFile ?? (storage is SqliteStorage file ? Path.Combine(Path.GetDirectoryName(file.Path) ?? contentRoot, "keys", "mandate-signing.key") : null)))
            .AddSingleton<IRunnerDirectory>(new ConfiguredRunners(options))
            .AddSingleton<IFactsSource, ConservativeFacts>()
            .AddSingleton<IApprovalCheck, UnprovedApprovals>()
            .AddSingleton<IStopSignal>(provider => new KillSwitchSignal(provider.GetRequiredService<IKillSwitchControl>()))
            .AddSingleton<IMissionAudit>(provider => new AuditTrailMissions(provider.GetRequiredService<IAuditSink>(), provider.GetRequiredService<TimeProvider>()))
            .AddHostedService<MissionSweeper>()
            .AddSingleton(provider => new ProblemConductor(
                provider.GetRequiredService<MandateSigner>(), provider.GetRequiredService<IMissionStore>(), provider.GetRequiredService<IMandateLedger>(), provider.GetRequiredService<IProblemStore>(),
                provider.GetRequiredService<IDossierStore>(), provider.GetRequiredService<IFactsSource>(), provider.GetRequiredService<IRunnerDirectory>(), provider.GetRequiredService<IApprovalCheck>(),
                provider.GetRequiredService<IStopSignal>(), provider.GetRequiredService<IMissionAudit>(), provider.GetRequiredService<TimeProvider>(),
                options.Conductor(), () => Guid.NewGuid().ToString("N")));
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
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllBytes(file, signer.PrivateKey());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return signer;
    }
}

internal sealed class ConfiguredRunners(MissionsOptions options) : IRunnerDirectory
{
    public string RunnerFor(World world, string applicationId) => world switch
    {
        World.Staging => options.StagingRunner,
        World.Git => options.GitRunner,
        _ => options.ReleaseRunner,
    };
}

/// <summary>
/// Until the control plane can fetch the real facts (the diff and the CI status from the forge on the exact commit, the health from Portarr), it states only what it knows by construction (the plan
/// covers the problem, because the problem was started from it). **A fact it does not know counts as failing**: the diff is not known to be clean, the budget accounting is not built, the pipelines and
/// the artifact are unknown, the claim is not checked. So no mandate for another world is granted on this source: it is for the demo and the tests to bring their own.
/// </summary>
internal sealed class ConservativeFacts : IFactsSource
{
    public PreconditionFacts Facts(string problemId, World world, Dossier? dossier) => new(
        world, PlanCovers: true, BudgetAvailable: false, KillSwitchEngaged: false, Staging: StagingVerdict.Usable, PreviousDossierExists: dossier is not null,
        DiffIsClean: null, CiGreenOnExactCommit: null, ArtifactIsTheOneProvedInStaging: null, WorkerClaimMatchesFacts: false, Loop: LoopVerdict.Allowed);
}

/// <summary>The maintainer's approval is proved by the second factor, recorded through the queue of approvals (ADR 0031 point 7). The plan service that links a plan entry to an approval is not built: until then none is proved, so no real mandate is issued.</summary>
internal sealed class UnprovedApprovals : IApprovalCheck
{
    public bool IsProved(PlanEntry entry) => false;
}

internal sealed class KillSwitchSignal(IKillSwitchControl killSwitch) : IStopSignal
{
    public bool Engaged => killSwitch.Engaged;
}

/// <summary>Writes the events of the missions to the audit trail, in order, with the others (ADR 0013).</summary>
internal sealed class AuditTrailMissions(IAuditSink sink, TimeProvider time) : IMissionAudit
{
    public void Record(string eventName, string subject, string? detail) =>
        sink.TryRecordAsync(new AuditEntry(time.GetUtcNow(), eventName, subject.Length > 120 ? subject[..120] : subject, null, detail is { Length: > 400 } ? detail[..400] : detail), CancellationToken.None).AsTask().GetAwaiter().GetResult();
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
                    conductor.Sweep();
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
