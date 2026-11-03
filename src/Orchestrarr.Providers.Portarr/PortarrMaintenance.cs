namespace Orchestrarr.Providers.Portarr;

// What Orchestrarr reads of the maintenance endpoint of Portarr (GET /api/v1/maintenance). Properties it does not use are left out.
// The contract belongs to Portarr (its MaintenanceResource), so the names follow it.

/// <summary><c>State</c> is Portarr's scanning state: on, off or unknown.</summary>
internal sealed record PortarrAlertScan(string? State = null, int Critical = 0, int High = 0, int Medium = 0, int Low = 0, int Other = 0, int Total = 0);

internal sealed record PortarrCiRun(string? Status = null, string? Conclusion = null, bool Failed = false);

internal sealed record PortarrRepository(
    string FullName,
    Uri? Url = null,
    bool Archived = false,
    bool Fork = false,
    PortarrAlertScan? Dependabot = null,
    PortarrAlertScan? CodeScanning = null,
    PortarrAlertScan? SecretScanning = null,
    int? DependabotPullRequests = null,
    PortarrCiRun? Ci = null,
    IReadOnlyList<string>? AppIds = null,
    IReadOnlyList<string>? Images = null);

internal sealed record PortarrImageUser(string EnvironmentId, string EnvironmentName, string Name, string? Namespace = null);

internal sealed record PortarrImage(
    string Reference,
    string? Status = null,
    string? Current = null,
    string? Latest = null,
    string? Change = null,
    IReadOnlyList<PortarrImageUser>? Users = null,
    string? RepositoryFullName = null);

internal sealed record PortarrMaintenance(
    IReadOnlyList<PortarrRepository>? Repositories = null,
    IReadOnlyList<PortarrImage>? Images = null);
