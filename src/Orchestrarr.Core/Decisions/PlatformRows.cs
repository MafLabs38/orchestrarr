using Orchestrarr.Core.Infrastructure;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Core.Decisions;

/// <summary>
/// The rows about the platforms of the description (ADR 0028): the K3s whose version is a variable of the code. A platform upgrade touches everything that runs on it, so it is
/// told and described and never offered (the version is in the text of the row, there is no change to show): one patch of the same minor, and the next minor only, because Kubernetes is upgraded one minor at a time.
/// </summary>
public static class PlatformRows
{
    /// <summary>The start of the identifier of a row about a platform: such a row exists only while the releases are known.</summary>
    public const string RowPrefix = "platform:";

    public static IReadOnlyList<ProblemRow> Build(IReadOnlyList<PlatformScope> platforms, InfrastructureIndex index, K3sReleases? releases)
    {
        ArgumentNullException.ThrowIfNull(platforms);
        ArgumentNullException.ThrowIfNull(index);
        var rows = new List<ProblemRow>();
        foreach (var platform in platforms)
        {
            if (platform is not { Kind: "k3s", Version: { Key: { } key } pointer })
            {
                continue;
            }
            // A repository that could not be read is told on the table already: its variable is not "missing"
            if (index.Problems.Any(problem => problem.Repository == pointer.Repository))
            {
                continue;
            }
            if (index.Content(pointer.Repository, pointer.Path) is not { } content || VersionParsers.Variable(content, key) is not var (line, written))
            {
                // The variable is not where the description says: told only when the code of the scope was read at all
                if (index.InScope)
                {
                    rows.Add(Row(platform, "unreadable", $"{key} not found in {pointer.Path}", null, CeilingReason.PlatformVersionNotRead, null));
                }
                continue;
            }
            if (K3sVersion.Parse(written) is not { } current || releases is null)
            {
                continue;
            }
            var location = CodeLocation.OfVariable(pointer.Repository, pointer.Path, line, key, written);
            if (releases.LatestByMinor.GetValueOrDefault(current.Minor) is { } patch && patch > current)
            {
                rows.Add(Row(platform, "patch", $"{current} → {patch}", UpdateKind.Patch, CeilingReason.PlatformUpgradeIsPlanned, location));
            }
            if (releases.LatestByMinor.GetValueOrDefault(current.Minor + 1) is { } next && next.Major == current.Major)
            {
                rows.Add(Row(platform, "minor", $"{current} → {next}", UpdateKind.Minor, CeilingReason.PlatformUpgradeIsPlanned, location));
            }
        }
        return rows;
    }

    private static ProblemRow Row(PlatformScope platform, string part, string description, UpdateKind? kind, CeilingReason why, CodeLocation? location) =>
        new(
            Id: $"{RowPrefix}{platform.Id}:{part}", WorkItemId: $"{RowPrefix}{platform.Id}", Application: platform.Id, Kind: ProblemKind.PlatformUpdate, Description: description,
            Severity: Severity.Info, Count: 1, UpdateClass: kind, Checks: null, Url: null, PullRequest: null, HeadSha: null, GoesUpTo: Ceiling.NoAction, Why: why, CanStopBeforeProduction: false,
            Locations: location is null ? null : [location]);
}
