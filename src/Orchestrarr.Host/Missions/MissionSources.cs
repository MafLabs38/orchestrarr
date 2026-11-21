using System.Security.Cryptography;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Agents;
using Orchestrarr.Host.Storage;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Missions;

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
