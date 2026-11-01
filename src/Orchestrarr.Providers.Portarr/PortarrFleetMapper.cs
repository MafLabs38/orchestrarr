using System.Text.Json;
using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Providers.Portarr;

/// <summary>Maps Portarr's maintenance report to the fleet model of Orchestrarr.</summary>
internal static class PortarrFleetMapper
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static FleetSnapshot Map(PortarrMaintenance report, DateTimeOffset readAt)
    {
        var repositories = (report.Repositories ?? []).Select(MapRepository).ToList();
        var images = (report.Images ?? []).Select(MapImage).ToList();
        return new FleetSnapshot(readAt, repositories, images);
    }

    private static RepositoryState MapRepository(PortarrRepository repository)
    {
        return new RepositoryState(
            FullName: repository.FullName,
            Url: repository.Url,
            Archived: repository.Archived,
            DependencyAlerts: MapAlerts(repository.Dependabot),
            CodeScanningAlerts: MapAlerts(repository.CodeScanning),
            SecretScanningAlerts: repository.SecretScanning?.Total ?? 0,
            OpenDependencyPullRequests: repository.DependabotPullRequests ?? 0,
            Ci: MapCi(repository.Ci),
            AppIds: repository.AppIds ?? [],
            ImageReferences: repository.Images ?? []);
    }

    // "Other" alerts have no severity: they are not counted, as Portarr does not rank them either
    private static AlertCounts MapAlerts(PortarrAlertScan? scan)
    {
        return scan is null ? AlertCounts.None : new AlertCounts(scan.Critical, scan.High, scan.Medium, scan.Low);
    }

    private static CiState MapCi(PortarrCiRun? run)
    {
        if (run is null)
        {
            return CiState.Unknown;
        }

        if (run.Failed)
        {
            return CiState.Failing;
        }

        if (run.Conclusion is null)
        {
            return string.IsNullOrEmpty(run.Status) ? CiState.Unknown : CiState.Running;
        }

        return string.Equals(run.Conclusion, "success", StringComparison.OrdinalIgnoreCase) ? CiState.Passing : CiState.Unknown;
    }

    private static ImageState MapImage(PortarrImage image)
    {
        return new ImageState(
            Reference: image.Reference,
            Current: image.Current,
            Latest: image.Latest,
            Status: MapStatus(image.Status),
            Change: MapChange(image.Change),
            RepositoryFullName: image.RepositoryFullName,
            Placements: [.. (image.Users ?? []).Select(u => new Placement(u.EnvironmentId, u.EnvironmentName, u.Namespace, u.Name))]);
    }

    private static ImageStatus MapStatus(string? status)
    {
        return status?.ToLowerInvariant() switch
        {
            "up-to-date" => ImageStatus.UpToDate,
            "behind" => ImageStatus.Behind,
            "newer-build" => ImageStatus.NewerBuild,
            "floating" => ImageStatus.Floating,
            "pending" => ImageStatus.Pending,
            _ => ImageStatus.Unknown,
        };
    }

    private static ImageChange MapChange(string? change)
    {
        return change?.ToLowerInvariant() switch
        {
            "build" => ImageChange.Build,
            "patch" => ImageChange.Patch,
            "minor" => ImageChange.Minor,
            "major" => ImageChange.Major,
            _ => ImageChange.None,
        };
    }
}
