using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Core.WorkItems;

/// <summary>Turns what a source says about the fleet into work items: findings grouped per repository or per image, with a plan.</summary>
public static class WorkItemBuilder
{
    public const string NoPlacementReason = "The environment description does not say where this runs.";

    public static IReadOnlyList<WorkItem> Build(FleetSnapshot fleet)
    {
        ArgumentNullException.ThrowIfNull(fleet);

        var items = new List<WorkItem>();
        var imagesOfRepositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var repository in fleet.Repositories.Where(r => !r.Archived))
        {
            var images = fleet.Images
                .Where(i => string.Equals(i.RepositoryFullName, repository.FullName, StringComparison.OrdinalIgnoreCase)
                    || repository.ImageReferences.Contains(i.Reference, StringComparer.OrdinalIgnoreCase))
                .ToList();
            foreach (var image in images)
            {
                imagesOfRepositories.Add(image.Reference);
            }

            var findings = RepositoryFindings(repository);
            findings.AddRange(images.Select(OutdatedImageFinding).OfType<Finding>());
            if (findings.Count == 0)
            {
                continue;
            }

            var placements = DistinctPlacements(images.SelectMany(i => i.Placements));
            items.Add(new WorkItem(
                Id: $"repository:{repository.FullName}",
                Title: repository.FullName,
                RepositoryFullName: repository.FullName,
                AppIds: repository.AppIds,
                Severity: findings.Max(f => f.Severity),
                Findings: findings,
                Plan: RepositoryPlan(repository, placements),
                Placements: placements));
        }

        // Images that are not built from a repository of the fleet (third-party): only an update is possible
        foreach (var image in fleet.Images.Where(i => !imagesOfRepositories.Contains(i.Reference)))
        {
            if (OutdatedImageFinding(image) is not { } finding)
            {
                continue;
            }

            var placements = DistinctPlacements(image.Placements);
            items.Add(new WorkItem(
                Id: $"image:{image.Reference}",
                Title: image.Reference,
                RepositoryFullName: null,
                AppIds: [],
                Severity: finding.Severity,
                Findings: [finding],
                Plan: [.. DeploySteps(placements)],
                Placements: placements));
        }

        return [.. items.OrderByDescending(i => i.Severity).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)];
    }

    private static List<Finding> RepositoryFindings(RepositoryState repository)
    {
        var findings = new List<Finding>();

        if (repository.DependencyAlerts.Total > 0)
        {
            findings.Add(new Finding(FindingKind.DependencyAlerts, AlertSeverity(repository.DependencyAlerts), repository.DependencyAlerts.Total,
                $"{Plural(repository.DependencyAlerts.Total, "dependency alert")} ({Breakdown(repository.DependencyAlerts)})"));
        }

        if (repository.CodeScanningAlerts.Total > 0)
        {
            findings.Add(new Finding(FindingKind.CodeScanningAlerts, AlertSeverity(repository.CodeScanningAlerts), repository.CodeScanningAlerts.Total,
                $"{Plural(repository.CodeScanningAlerts.Total, "code scanning alert")} ({Breakdown(repository.CodeScanningAlerts)})"));
        }

        if (repository.SecretScanningAlerts > 0)
        {
            findings.Add(new Finding(FindingKind.SecretScanningAlerts, Severity.Danger, repository.SecretScanningAlerts,
                Plural(repository.SecretScanningAlerts, "exposed secret")));
        }

        if (repository.OpenDependencyPullRequests > 0)
        {
            findings.Add(new Finding(FindingKind.OpenDependencyPullRequests, Severity.Info, repository.OpenDependencyPullRequests,
                $"{Plural(repository.OpenDependencyPullRequests, "open dependency pull request")} waiting"));
        }

        if (repository.Ci == CiState.Failing)
        {
            findings.Add(new Finding(FindingKind.FailingCi, Severity.Warning, 1, "The CI of the default branch is failing"));
        }

        return findings;
    }

    private static Finding? OutdatedImageFinding(ImageState image)
    {
        if (image.Status is not (ImageStatus.Behind or ImageStatus.NewerBuild))
        {
            return null;
        }

        var severity = image.Change == ImageChange.Major ? Severity.Warning : Severity.Info;
        var move = image.Current is not null && image.Latest is not null ? $"{image.Current} → {image.Latest}" : "a newer build exists";
        var change = image.Change is ImageChange.None ? string.Empty : $" ({image.Change.ToString().ToLowerInvariant()})";
        return new Finding(FindingKind.OutdatedImage, severity, 1, $"{image.Reference}: {move}{change}");
    }

    private static List<PlanStep> RepositoryPlan(RepositoryState repository, IReadOnlyList<Placement> placements)
    {
        var plan = new List<PlanStep>
        {
            new(StepKind.Validate, "Build and test the change", null, Ticked: true, Available: true, Reason: null),
            new(StepKind.Merge, repository.OpenDependencyPullRequests > 0 ? "Merge the pull requests that pass" : "Merge the fix", null, Ticked: true, Available: true, Reason: null),
        };
        plan.AddRange(DeploySteps(placements));
        return plan;
    }

    private static IEnumerable<PlanStep> DeploySteps(IReadOnlyList<Placement> placements)
    {
        if (placements.Count == 0)
        {
            yield return new PlanStep(StepKind.Deploy, "Deploy", null, Ticked: false, Available: false, Reason: NoPlacementReason);
            yield break;
        }

        foreach (var placement in placements)
        {
            yield return new PlanStep(StepKind.Deploy, $"Deploy to {placement.EnvironmentName}", placement.EnvironmentId, Ticked: true, Available: true, Reason: null);
        }
    }

    private static List<Placement> DistinctPlacements(IEnumerable<Placement> placements)
    {
        return [.. placements
            .GroupBy(p => p.EnvironmentId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.EnvironmentName, StringComparer.OrdinalIgnoreCase)];
    }

    private static Severity AlertSeverity(AlertCounts counts)
    {
        if (counts.Critical > 0 || counts.High > 0)
        {
            return Severity.Danger;
        }

        return counts.Medium > 0 ? Severity.Warning : Severity.Info;
    }

    private static string Breakdown(AlertCounts counts)
    {
        var parts = new List<string>();
        if (counts.Critical > 0)
        {
            parts.Add($"{counts.Critical} critical");
        }

        if (counts.High > 0)
        {
            parts.Add($"{counts.High} high");
        }

        if (counts.Medium > 0)
        {
            parts.Add($"{counts.Medium} medium");
        }

        if (counts.Low > 0)
        {
            parts.Add($"{counts.Low} low");
        }

        return string.Join(", ", parts);
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
