using System.Text.Json;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>
/// **Test bed only**: the findings about the dependencies of a repository, read from a JSON file at each call (ADR 0048). It is the fake source of advisories, like the fake Portarr is the fake source of the fleet: the lab plants a case by
/// writing the file. A file that is missing or unreadable says nothing (an empty list), never a guess; the text of a finding is data for the lead, bounded and flattened when it is read.
/// </summary>
internal sealed class FileDependencyFindings(string path) : IDependencyFindings
{
    private const int MaxFindings = 50;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DependencyFinding>> ReadAsync(string repository, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var all = await JsonSerializer.DeserializeAsync<Dictionary<string, List<DependencyFinding>>>(stream, Json, cancellationToken);
            return all is not null && all.TryGetValue(repository, out var found) ? [.. found.Where(finding => finding is { Kind.Length: > 0, Package.Length: > 0 }).Take(MaxFindings)] : [];
        }
        catch (Exception failure) when (failure is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
