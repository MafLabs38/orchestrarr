using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Core.Test;

internal static class FleetBuilders
{
    public static readonly DateTimeOffset Now = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    public static RepositoryState Repository(
        string fullName = "example/app",
        bool archived = false,
        AlertCounts? dependencyAlerts = null,
        AlertCounts? codeScanning = null,
        int secrets = 0,
        int pullRequests = 0,
        CiState ci = CiState.Passing,
        string[]? images = null)
    {
        return new RepositoryState(fullName, null, archived, dependencyAlerts ?? AlertCounts.None, codeScanning ?? AlertCounts.None,
            secrets, pullRequests, ci, [], images ?? []);
    }

    public static ImageState Image(
        string reference = "ghcr.io/example/app:1.0.0",
        ImageStatus status = ImageStatus.Behind,
        ImageChange change = ImageChange.Minor,
        string? repository = null,
        params Placement[] placements)
    {
        return new ImageState(reference, "1.0.0", "1.1.0", status, change, repository, placements);
    }

    public static Placement Place(string environmentId, string? environmentName = null)
    {
        return new Placement(environmentId, environmentName ?? environmentId, null, "workload");
    }

    public static FleetSnapshot Snapshot(IReadOnlyList<RepositoryState>? repositories = null, IReadOnlyList<ImageState>? images = null)
    {
        return new FleetSnapshot(Now, repositories ?? [], images ?? []);
    }
}
