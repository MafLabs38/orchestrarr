using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Infrastructure;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Decisions;

/// <summary>What the code of the scope changes about the row of an image: why it stops where it does, the places that write the version, and the version a change would write.</summary>
internal sealed record CodeVerdict(CeilingReason Why, IReadOnlyList<CodeLocation>? Locations, string? ProposedTag);

/// <summary>
/// Where the version of an image is written in the code of the scope, and what that says about the row (ADR 0028). Without a scope nothing is said about the code.
/// The steps are a sequence of guards, and the order carries the meaning: not in the scope, written by an automation, a version that differs from what runs, a pin by
/// digest, several environments, a major version, and last a version that can be proposed.
/// </summary>
internal static class ImageCode
{
    public static CodeVerdict Of(WorkItem item, Finding finding, CeilingReason without, InfrastructureIndex? infrastructure)
    {
        if (infrastructure is not { InScope: true } || finding.Image is not { } update || ImageReference.Parse(update.Reference) is not { } reference || without == CeilingReason.ManagedByPlatform)
        {
            return new CodeVerdict(without, null, null);
        }
        // A repository that deploys to one environment is about the workloads of that environment, not of another one: the placements of this image, not of the whole item
        var placements = update.Placements ?? item.Placements;
        var places = PlacesOf(infrastructure, reference, update, placements);
        return Guard(finding, update, placements, places, infrastructure.Problems.Count > 0) ?? Propose(finding, update, reference, places.Locations, without);
    }

    // The same image can be written at several versions (a database of two majors): the places that write the version that runs are the ones the row is about
    private static Places PlacesOf(InfrastructureIndex infrastructure, ImageReference reference, ImageUpdate update, IReadOnlyList<Placement> placements)
    {
        var everywhere = infrastructure.Find(reference).Where(location => location.Environment is null || placements.Count == 0 || placements.Any(placement => placement.EnvironmentId == location.Environment)).ToList();
        var running = reference.Tag ?? update.Current;
        var exact = everywhere.Where(location => string.Equals(location.Image.Tag, running, StringComparison.OrdinalIgnoreCase)).ToList();
        return new Places(exact.Count > 0 ? exact : everywhere, exact.Count > 0);
    }

    // The reasons that stop a change before a version is even considered; null when none does
    private static CodeVerdict? Guard(Finding finding, ImageUpdate update, IReadOnlyList<Placement> placements, Places places, bool someRepositoryUnreadable)
    {
        var locations = places.Locations;
        if (locations.Count == 0)
        {
            // With a repository that could not be read, "written nowhere" is not known
            return new CodeVerdict(someRepositoryUnreadable ? CeilingReason.CodeNotFullyRead : CeilingReason.NotInScope, [], null);
        }
        var ours = locations.Where(location => location.Managed is null).ToList();
        if (ours.Count == 0)
        {
            return new CodeVerdict(CeilingReason.ManagedByAutomation, locations, null);
        }
        if (finding.Kind == FindingKind.OutdatedImage && !places.RunningIsWritten && update.Current is not null)
        {
            return new CodeVerdict(CeilingReason.CodeDiffersFromRunning, locations, null);
        }
        if (ours.Any(location => location.Image.Digest is not null))
        {
            return new CodeVerdict(CeilingReason.PinnedByDigest, locations, null);
        }
        // One environment at a time: several placements, or places that belong to several environments when the placements are not known, propose nothing
        var environments = placements.Select(placement => placement.EnvironmentId).Concat(ours.Select(location => location.Environment).OfType<string>()).Distinct(StringComparer.Ordinal).Count();
        return environments > 1 ? new CodeVerdict(CeilingReason.SeveralEnvironments, locations, null) : null;
    }

    private static CodeVerdict Propose(Finding finding, ImageUpdate update, ImageReference reference, IReadOnlyList<CodeLocation> locations, CeilingReason without)
    {
        if (without == CeilingReason.MajorNeedsPlan)
        {
            return new CodeVerdict(without, locations, null);
        }
        // An outdated image goes to the latest version; a floating one is pinned to the version that runs, which changes nothing that is deployed
        var proposed = finding.Kind == FindingKind.OutdatedImage ? update.Latest : update.Current;
        // A tag that is not a version (latest, a branch) and the tag that is already written change nothing: nothing is proposed
        var usable = ImageReference.IsTag(proposed) && !string.Equals(proposed, reference.Tag, StringComparison.OrdinalIgnoreCase) && ImageReference.Parse($"{reference.Name}:{proposed}") is { IsFloating: false };
        return usable ? new CodeVerdict(CeilingReason.CodeChangeShown, locations, proposed) : new CodeVerdict(without, locations, null);
    }

    private sealed record Places(IReadOnlyList<CodeLocation> Locations, bool RunningIsWritten);
}
