using System.Text.Json;
using Orchestrarr.Core.Fleet;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// **Test bed only**: the fake source of findings goes as far as the rows (ADR 0055). The lab writes advisories that no real scanner reports (for example a flaw in an image for which no fixed version exists, the grain of sand of
/// the demo) in the same file as the details of the alerts (<see cref="FileDependencyFindings"/>); a repository that the file names is counted as having at least that many dependency alerts, so that its row exists. The
/// real source is read first and nothing it says is lowered. The icons are the real source's.
/// </summary>
internal sealed class SandboxFleetFindings(IFleetSource inner, string path) : IFleetSource, IIconSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => inner.Kind;

    public bool Configured => inner.Configured;

    public Task<IconImage?> GetAsync(string name, CancellationToken cancellationToken) =>
        inner is IIconSource icons ? icons.GetAsync(name, cancellationToken) : Task.FromResult<IconImage?>(null);

    public async Task<FleetSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await inner.ReadAsync(cancellationToken);
        var planted = await PlantedAsync(cancellationToken);
        if (planted.Count == 0)
        {
            return snapshot;
        }
        return snapshot with
        {
            Repositories = [.. snapshot.Repositories.Select(repository => planted.TryGetValue(repository.FullName, out var count) && repository.DependencyAlerts.Counts.Total < count
                ? repository with { DependencyAlerts = AlertScan.Of(repository.DependencyAlerts.Counts with { Critical = repository.DependencyAlerts.Counts.Critical + count - repository.DependencyAlerts.Counts.Total }) }
                : repository)],
        };
    }

    // How many findings the lab wrote for each repository (a file that is missing or unreadable plants nothing)
    private async Task<Dictionary<string, int>> PlantedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var all = await JsonSerializer.DeserializeAsync<Dictionary<string, List<DependencyFinding>>>(stream, Json, cancellationToken);
            return all?.ToDictionary(pair => pair.Key, pair => pair.Value.Count, StringComparer.OrdinalIgnoreCase) ?? [];
        }
        catch (Exception failure) when (failure is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
