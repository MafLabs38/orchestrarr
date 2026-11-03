using System.Text.Json;
using System.Text.Json.Serialization;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Providers.GitHub;

/// <summary>Reads pull requests from a file, by repository, in the shape of Orchestrarr's own API. For a demo, tests and working offline.</summary>
public sealed class SamplePullRequestSource : IPullRequestSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;

    public SamplePullRequestSource(string path)
    {
        _path = path;
    }

    public string Kind => "sample";

    public bool Configured => true;

    public async Task<IReadOnlyList<PullRequestInfo>> ListDependencyPullRequestsAsync(string repositoryFullName, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(_path);
        var all = await JsonSerializer.DeserializeAsync<Dictionary<string, List<PullRequestInfo>>>(stream, Json, cancellationToken)
            ?? throw new InvalidOperationException($"The sample file {_path} is empty.");
        return all.TryGetValue(repositoryFullName, out var pullRequests) ? pullRequests : [];
    }
}
