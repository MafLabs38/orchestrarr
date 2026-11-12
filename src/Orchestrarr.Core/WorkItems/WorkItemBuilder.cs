using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Core.WorkItems;

/// <summary>
/// Turns what a source says about the fleet into work items: findings grouped per repository or per image, with a plan. The unit is the
/// repository, not the application (SPEC D-21): a fix, a merge and a build happen at the level of a repository.
/// </summary>
public static class WorkItemBuilder
{
    public static IReadOnlyList<WorkItem> Build(FleetSnapshot fleet, IPowerPolicy powers)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(powers);

        var items = new List<WorkItem>();
        var imagesOfRepositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imagesByReference = fleet.Images.ToLookup(i => i.Reference, StringComparer.OrdinalIgnoreCase);
        var imagesByRepository = fleet.Images.Where(i => i.RepositoryFullName is not null).ToLookup(i => i.RepositoryFullName!, StringComparer.OrdinalIgnoreCase);

        // A fork or an archived repository is not the operator's to maintain (SPEC Q-10 will make this an allow-list)
        foreach (var repository in fleet.Repositories.Where(r => r is { Archived: false, Fork: false }))
        {
            var images = imagesByRepository[repository.FullName]
                .Concat(repository.ImageReferences.SelectMany(reference => imagesByReference[reference]))
                .DistinctBy(i => i.Reference, StringComparer.OrdinalIgnoreCase)
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
                Plan: RepositoryPlan(repository, findings, placements, powers),
                Placements: placements,
                PullRequests: PullRequestsRead.NotNeeded,
                LastPushAt: repository.LastPushAt));
        }

        // Images that are not built from a repository of the fleet (third-party): only an update is possible. It is deployed and
        // smoke tested like any deployment (SPEC D-07), with nothing to validate beforehand.
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
                AppIds: image.AppIds ?? [],
                Severity: finding.Severity,
                Findings: [finding],
                Plan: [.. DeploySteps(placements, null, powers)],
                Placements: placements,
                PullRequests: PullRequestsRead.NotNeeded));
        }

        return [.. items.OrderByDescending(i => i.Severity).ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)];
    }

    private static List<Finding> RepositoryFindings(RepositoryState repository)
    {
        var findings = new List<Finding>();

        AddAlerts(findings, FindingKind.DependencyAlerts, repository.DependencyAlerts);
        if (repository.DependencyAlerts.State != AlertScanState.On)
        {
            findings.Add(new Finding(FindingKind.DependencyScanUnavailable, Severity.Info, 1));
        }

        AddAlerts(findings, FindingKind.CodeScanningAlerts, repository.CodeScanningAlerts);

        if (repository.SecretScanningAlerts > 0)
        {
            findings.Add(new Finding(FindingKind.SecretScanningAlerts, Severity.Danger, repository.SecretScanningAlerts));
        }

        if (repository.OpenDependencyPullRequests > 0)
        {
            findings.Add(new Finding(FindingKind.OpenDependencyPullRequests, Severity.Info, repository.OpenDependencyPullRequests));
        }

        if (repository.Ci == CiState.Failing)
        {
            findings.Add(new Finding(FindingKind.FailingCi, Severity.Warning, 1));
        }

        return findings;
    }

    private static void AddAlerts(List<Finding> findings, FindingKind kind, AlertScan scan)
    {
        if (scan.State == AlertScanState.On && scan.Counts.Total > 0)
        {
            findings.Add(new Finding(kind, AlertSeverity(scan.Counts), scan.Counts.Total, scan.Counts));
        }
    }

    private static Finding? OutdatedImageFinding(ImageState image)
    {
        if (image.Status == ImageStatus.Floating)
        {
            return new Finding(FindingKind.FloatingImage, Severity.Info, 1, Image: new ImageUpdate(image.Reference, image.Current, null, ImageChange.None));
        }
        if (image.Status is not (ImageStatus.Behind or ImageStatus.NewerBuild))
        {
            return null;
        }

        var severity = image.Change == ImageChange.Major ? Severity.Warning : Severity.Info;
        return new Finding(FindingKind.OutdatedImage, severity, 1, Image: new ImageUpdate(image.Reference, image.Current, image.Latest, image.Change));
    }

    private static List<PlanStep> RepositoryPlan(RepositoryState repository, List<Finding> findings, IReadOnlyList<Placement> placements, IPowerPolicy powers)
    {
        // Nothing to fix by code (an exposed secret, a scan that is off): only a person can act, so there is no plan to approve
        var fixable = findings.Any(f => f.Kind is not (FindingKind.SecretScanningAlerts or FindingKind.DependencyScanUnavailable));
        if (!fixable)
        {
            return [];
        }

        var merge = repository.OpenDependencyPullRequests > 0 ? StepKind.MergePullRequests : StepKind.MergeFix;
        var plan = new List<PlanStep>
        {
            Step(StepKind.Validate, repository.FullName, null, null, available: true, reason: null, powers),
            Step(merge, repository.FullName, null, null, available: true, reason: null, powers),
        };
        if (repository.OpenDependencyPullRequests > 0)
        {
            // Never ticked by default: it calls an agent, so it is chosen
            plan.Add(new PlanStep(StepKind.Repair, null, null, Ticked: false, Available: true, Granted: powers.IsGranted(StepKind.Repair, repository.FullName, null), null));
        }
        plan.AddRange(DeploySteps(placements, repository.FullName, powers));
        return plan;
    }

    private static IEnumerable<PlanStep> DeploySteps(IReadOnlyList<Placement> placements, string? repositoryFullName, IPowerPolicy powers)
    {
        if (placements.Count == 0)
        {
            yield return Step(StepKind.Deploy, repositoryFullName, null, null, available: false, StepReason.NoPlacement, powers);
            yield break;
        }

        foreach (var placement in placements)
        {
            yield return Step(StepKind.Deploy, repositoryFullName, placement.EnvironmentId, placement.EnvironmentName, available: true, reason: null, powers);
        }
    }

    private static PlanStep Step(StepKind kind, string? repositoryFullName, string? environmentId, string? environmentName, bool available, StepReason? reason, IPowerPolicy powers)
    {
        return new PlanStep(kind, environmentId, environmentName, Ticked: available, Available: available, Granted: powers.IsGranted(kind, repositoryFullName, environmentId), reason);
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
}
