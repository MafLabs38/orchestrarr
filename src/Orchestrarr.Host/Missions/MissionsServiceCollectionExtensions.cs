using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Cryptography;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Machines;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Agents;
using Orchestrarr.Host.Storage;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Missions;

internal static class MissionsServiceCollectionExtensions
{
    /// <summary>The stores, the key that signs the mandates and the conductor of a problem (ADR 0030 to 0032), in the SQLite file or, in the sandbox, in memory.</summary>
    public static IServiceCollection AddMissions(this IServiceCollection services, IConfiguration configuration, IStorageSettings storage, string contentRoot, bool isDevelopment = false)
    {
        var options = configuration.GetSection(MissionsOptions.SectionName).Get<MissionsOptions>() ?? new MissionsOptions();
        services.AddSingleton(options);
        var agents = configuration.GetSection(AgentsOptions.SectionName).Get<AgentsOptions>() ?? new AgentsOptions();
        // Refused here, at start, not on the first use of the controller
        if (agents.LeadMaxRows < 1)
        {
            throw new InvalidOperationException("Agents:LeadMaxRows must be at least 1.");
        }
        var controllerTimeout = agents.ControllerTimeoutSeconds >= 10 ? TimeSpan.FromSeconds(agents.ControllerTimeoutSeconds) : throw new InvalidOperationException($"Agents:ControllerTimeoutSeconds must be at least 10 seconds, not {agents.ControllerTimeoutSeconds}.");
        services.AddSingleton(agents).AddSingleton<IAgentGateway>(provider => agents.Enabled ? new ClaudeCliGateway(agents, provider.GetRequiredService<ILogger<ClaudeCliGateway>>()) : new DisabledGateway())
            .AddSingleton(provider => new ControllerAgent(provider.GetRequiredService<IAgentGateway>(), agents.ControllerModel, controllerTimeout))
            .AddSingleton(provider => new LeadPlanner(provider.GetRequiredService<ControllerAgent>(), agents.LeadMaxConcurrent >= 1 ? agents.LeadMaxConcurrent : throw new InvalidOperationException("Agents:LeadMaxConcurrent must be at least 1.")));
        if (storage is SqliteStorage)
        {
            services
                .AddSingleton<IMissionStore>(provider => new SqliteMissionStore(provider.GetRequiredService<SqliteDatabase>()))
                .AddSingleton<IMandateLedger>(provider => new SqliteMandateLedger(provider.GetRequiredService<SqliteDatabase>()))
                .AddSingleton<IProblemStore>(provider => new SqliteProblemStore(provider.GetRequiredService<SqliteDatabase>()))
                .AddSingleton<IDossierStore>(provider => new SqliteDossierStore(provider.GetRequiredService<SqliteDatabase>()))
                .AddSingleton<IMissionUnit>(provider => new SqliteMissionUnit(provider.GetRequiredService<SqliteDatabase>()));
        }
        else
        {
            services.AddSingleton<IMissionStore, InMemoryMissionStore>().AddSingleton<IMandateLedger, InMemoryMandateLedger>()
                .AddSingleton<IProblemStore, InMemoryProblemStore>().AddSingleton<IDossierStore, InMemoryDossierStore>().AddSingleton<IMissionUnit, InMemoryMissionUnit>();
        }
        services
            .AddSingleton<ProblemLocks>()
            .AddSingleton<ProblemQueue>()
            .AddSingleton<ProblemParking>()
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
            .AddSingleton(provider => new MissionStores(provider.GetRequiredService<IMissionStore>(), provider.GetRequiredService<IMandateLedger>(), provider.GetRequiredService<IProblemStore>(), provider.GetRequiredService<IDossierStore>(), provider.GetRequiredService<IMissionUnit>()))
            .AddSingleton(provider => new PowerSources(provider.GetRequiredService<IApprovalSource>(), provider.GetRequiredService<ApprovalVerifier>(), provider.GetRequiredService<IApprovalWithdrawals>(), provider.GetRequiredService<IGrantSource>(), provider.GetRequiredService<IStopSignal>()))
            .AddSingleton(provider => new ConductorServices(provider.GetRequiredService<IFactsSource>(), provider.GetRequiredService<IRunnerDirectory>(), provider.GetRequiredService<IMissionAudit>(), provider.GetRequiredService<TimeProvider>(), () => Guid.NewGuid().ToString("N")))
            .AddSingleton(provider => new ProblemConductor(
                provider.GetRequiredService<MandateSigner>(), provider.GetRequiredService<MissionStores>(), provider.GetRequiredService<PowerSources>(), provider.GetRequiredService<ConductorServices>(), options.Conductor(), provider.GetRequiredService<ProblemLocks>()));
        services.AddSingleton<MissionProgress>();
        services.TryAddSingleton<IDependencyFindings>(_ => new NoDependencyFindings());
        if (options.Machines.Length > 0)
        {
            services.AddMachines(options, storage);
        }
        if (options.Sandbox)
        {
            services.AddSandboxMissions(configuration, isDevelopment).AddHostedService<MissionDriver>().AddHostedService<ProblemQueueDriver>();
        }
        return services;
    }

    private static void AddMachines(this IServiceCollection services, MissionsOptions options, IStorageSettings storage)
    {
        if (options.Machines.Any(machine => string.IsNullOrWhiteSpace(machine.Id) || string.IsNullOrWhiteSpace(machine.Runner)) || options.Machines.Select(machine => machine.Id).Distinct(StringComparer.Ordinal).Count() != options.Machines.Length)
        {
            throw new InvalidOperationException("Missions:Machines: each machine needs a distinct Id and a Runner.");
        }
        if (options.MachineLeaseMinutes < 1)
        {
            throw new InvalidOperationException("Missions:MachineLeaseMinutes must be at least 1.");
        }
        if (storage is SqliteStorage)
        {
            services.AddSingleton<IMachineStore>(provider => new SqliteMachineStore(provider.GetRequiredService<SqliteDatabase>()));
        }
        else
        {
            services.AddSingleton<IMachineStore, InMemoryMachineStore>();
        }
        services
            .AddSingleton(provider => new MachinePool(provider.GetRequiredService<IMachineStore>(), provider.GetRequiredService<TimeProvider>(), new MachineOptions(TimeSpan.FromMinutes(options.MachineLeaseMinutes))))
            .AddHostedService<MachineKeeper>();
    }
}
