using System.Security.Cryptography;
using Orchestrarr.Core.Approvals;
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
            .AddSingleton(provider => new ProblemConductor(
                provider.GetRequiredService<MandateSigner>(), provider.GetRequiredService<IMissionStore>(), provider.GetRequiredService<IMandateLedger>(), provider.GetRequiredService<IProblemStore>(),
                provider.GetRequiredService<IDossierStore>(), provider.GetRequiredService<IFactsSource>(), provider.GetRequiredService<IRunnerDirectory>(), provider.GetRequiredService<TimeProvider>(),
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
/// Until the control plane can fetch the real facts (the diff and the CI status from the forge on the exact commit, the health from Portarr), it states only what it knows: the global
/// stop, and nothing else. A fact it does not know is not a fact in favour: no mandate for another world is granted on it.
/// </summary>
internal sealed class ConservativeFacts(IKillSwitchControl killSwitch) : IFactsSource
{
    public PreconditionFacts Facts(string problemId, World world, Dossier? dossier) => new(
        world, PlanCovers: true, BudgetAvailable: true, KillSwitchEngaged: killSwitch.Engaged, Staging: StagingVerdict.Usable, PreviousDossierExists: dossier is not null,
        DiffTouchesForbiddenPath: false, CiGreenOnExactCommit: null, ArtifactIsTheOneProvedInStaging: null, WorkerClaimMatchesFacts: false, Loop: LoopVerdict.Allowed);
}
