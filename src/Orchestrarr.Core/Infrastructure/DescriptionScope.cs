using System.Text.Json;

namespace Orchestrarr.Core.Infrastructure;

/// <summary>An environment of the description: the same identifier as in Portarr.</summary>
public sealed record EnvironmentScope(string Id, string? Name);

/// <summary>
/// The perimeter of the work, as the description says (ADR 0028): the environments and the repositories where versions are written. Orchestrarr
/// reads no code outside of it (the findings of the sources are not filtered by it yet). Anything wrong in it stops the start, with the place of the mistake.
/// </summary>
public sealed record DescriptionScope(IReadOnlyList<EnvironmentScope> Environments, IReadOnlyList<RepositoryScope> Repositories)
{
    public static DescriptionScope None { get; } = new([], []);

    public static DescriptionScope Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException failure)
        {
            throw new InvalidOperationException($"The description cannot be read: {failure.Message}", failure);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("The description is an object with its sections.");
            }
            var environments = Section(root, "environments").Select(Environment).ToList();
            Distinct(environments.Select(environment => environment.Id), "environments");
            var repositories = Section(root, "repositories").Select(element => Repository(element, environments)).ToList();
            Distinct(repositories.Select(repository => $"{repository.Repository} ({repository.Kind}, {repository.Environment ?? "any environment"})"), "repositories");
            return new DescriptionScope(environments, repositories);
        }
    }

    private static IEnumerable<JsonElement> Section(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var section) || section.ValueKind == JsonValueKind.Null)
        {
            return [];
        }
        return section.ValueKind == JsonValueKind.Array ? section.EnumerateArray() : throw new InvalidOperationException($"'{name}' is a list.");
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void Distinct(IEnumerable<string> ids, string section)
    {
        var duplicate = ids.GroupBy(id => id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"'{duplicate.Key}' is in '{section}' twice.");
        }
    }

    private static EnvironmentScope Environment(JsonElement element) =>
        Text(element, "id") is { Length: > 0 } id ? new EnvironmentScope(id, Text(element, "name")) : throw new InvalidOperationException("An environment has an id.");

    private static bool IsRepositoryName(string name) => name.Count(c => c == '/') == 1 && !name.StartsWith('/') && !name.EndsWith('/') && !name.Contains(' ', StringComparison.Ordinal);

    private static string? EnvironmentOf(JsonElement element, List<EnvironmentScope> environments, string what)
    {
        var environment = Text(element, "environment");
        if (environment is not null && !environments.Any(known => known.Id == environment))
        {
            throw new InvalidOperationException($"{what} deploys to the environment '{environment}', which is not in the environments of the description.");
        }
        return environment;
    }

    private static RepositoryScope Repository(JsonElement element, List<EnvironmentScope> environments)
    {
        var repository = Text(element, "repository");
        if (repository is null || !IsRepositoryName(repository))
        {
            throw new InvalidOperationException("A repository is written owner/name.");
        }
        var kind = Text(element, "kind");
        if (kind is null || !VersionParsers.Kinds.Contains(kind))
        {
            throw new InvalidOperationException($"The kind of '{repository}' is one of {string.Join(", ", VersionParsers.Kinds)}.");
        }
        var entries = element.TryGetProperty("paths", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [];
        if (entries.Any(entry => entry.ValueKind != JsonValueKind.String))
        {
            throw new InvalidOperationException($"The paths of '{repository}' are text.");
        }
        var paths = entries.Select(entry => entry.GetString() ?? string.Empty).ToList();
        if (paths.Count == 0 || paths.Any(path => string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains("..", StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"'{repository}' names the paths it is read from, relative to the repository.");
        }
        return new RepositoryScope(repository, kind, paths, EnvironmentOf(element, environments, $"'{repository}'"));
    }
}
