using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Core.Applications;

/// <summary>How an application can be told to work after a change (ADR 0020). Unknown: the description does not say, and nothing is guessed.</summary>
public enum QualifyMode
{
    Unknown,
    None,
    Health,
    Smoke,
    E2E,
}

/// <summary>
/// What may be done to an application, as its description says (ADR 0020): only <c>Id</c>, <c>Kind</c> and <c>Source</c> are required, a missing
/// field is unknown. The description grants no power: the runner's rights list does (ADR 0017).
/// </summary>
/// <param name="Id">The work item it describes, for example <c>repository:example/shop</c>.</param>
/// <param name="Kind"><c>own</c> or <c>thirdParty</c>.</param>
/// <param name="Source">Where the new version comes from: a repository, or an image.</param>
/// <param name="Qualify">How to know that it works after a change.</param>
/// <param name="CanRollBack">How the previous version is restored is described.</param>
/// <param name="HasCheckAfterDeploy">A check after the deployment is described.</param>
/// <param name="PromotesAutomatically">The classes of update that may reach production by themselves; the others stop before.</param>
public sealed record ApplicationDescription(
    string Id,
    string Kind,
    string Source,
    QualifyMode Qualify,
    bool CanRollBack,
    bool HasCheckAfterDeploy,
    IReadOnlyList<UpdateKind> PromotesAutomatically);

/// <summary>The descriptions the product knows, and a version that changes whenever one of them does: an approval records it (ADR 0019).</summary>
public interface IApplicationCatalog
{
    /// <summary>A short identifier of the content of the descriptions; empty when there are none.</summary>
    string Version { get; }

    ApplicationDescription? Find(string workItemId);
}

public static class ApplicationCatalogExtensions
{
    /// <summary>The description of a work item: under its own identifier, or under the identifier of one of the applications that Portarr says it is.</summary>
    public static ApplicationDescription? Find(this IApplicationCatalog catalog, Orchestrarr.Core.WorkItems.WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(item);
        return catalog.Find(item.Id) ?? item.AppIds.Select(catalog.Find).FirstOrDefault(description => description is not null);
    }
}

public sealed class EmptyApplicationCatalog : IApplicationCatalog
{
    public string Version => string.Empty;

    public ApplicationDescription? Find(string workItemId) => null;
}

/// <summary>The descriptions read from a file of the private configuration (JSON for now; a provider that reads the repository comes later).</summary>
public sealed class ApplicationCatalog : IApplicationCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) } };
    private readonly Dictionary<string, ApplicationDescription> _byId;

    private ApplicationCatalog(Dictionary<string, ApplicationDescription> byId, string version)
    {
        _byId = byId;
        Version = version;
    }

    public string Version { get; }

    public ApplicationDescription? Find(string workItemId) => _byId.GetValueOrDefault(workItemId);

    /// <summary>Reads the file. A description that is wrong stops the start: it is checked now, not when it is needed.</summary>
    public static ApplicationCatalog Parse(string content)
    {
        FileShape? file;
        try
        {
            file = JsonSerializer.Deserialize<FileShape>(content, Json);
        }
        catch (JsonException failure)
        {
            throw new InvalidOperationException($"The descriptions of the applications cannot be read: {failure.Message}", failure);
        }

        var byId = new Dictionary<string, ApplicationDescription>(StringComparer.Ordinal);
        foreach (var raw in file?.Applications ?? [])
        {
            var description = Describe(raw);
            if (!byId.TryAdd(description.Id, description))
            {
                throw new InvalidOperationException($"The application '{description.Id}' is described twice.");
            }
        }
        return new ApplicationCatalog(byId, byId.Count == 0 ? string.Empty : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..12].ToLowerInvariant());
    }

    private static ApplicationDescription Describe(RawApplication raw)
    {
        var source = SourceOf(raw.Source);
        if (string.IsNullOrWhiteSpace(raw.Id) || string.IsNullOrWhiteSpace(raw.Kind) || string.IsNullOrWhiteSpace(source))
        {
            throw new InvalidOperationException($"The application '{raw.Id}' is described with at least an id, a kind and a source.");
        }
        // thirdParty, third-party: both are written
        var kind = raw.Kind == "third-party" ? "thirdParty" : raw.Kind;
        if (kind is not ("own" or "thirdParty"))
        {
            throw new InvalidOperationException($"The kind of '{raw.Id}' is own or thirdParty.");
        }
        return new ApplicationDescription(
            raw.Id,
            kind,
            source,
            raw.Qualify?.Mode ?? QualifyMode.Unknown,
            !string.IsNullOrWhiteSpace(raw.Promote?.Rollback),
            !string.IsNullOrWhiteSpace(raw.Promote?.Check),
            [.. (raw.Update?.Promote ?? []).Where(promoted => promoted is UpdateKind.Patch or UpdateKind.Minor or UpdateKind.Major).Distinct()]);
    }

    // The source is written as a name (a repository, an image) or as an object that says which: { repository: ... } or { image: ... }
    private static string? SourceOf(JsonElement? source) => source switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString(),
        { ValueKind: JsonValueKind.Object } fields when fields.TryGetProperty("repository", out var repository) && repository.ValueKind == JsonValueKind.String => repository.GetString(),
        { ValueKind: JsonValueKind.Object } fields when fields.TryGetProperty("image", out var image) && image.ValueKind == JsonValueKind.String => image.GetString(),
        _ => null,
    };

    private sealed record FileShape(List<RawApplication>? Applications);

    private sealed record RawApplication(string? Id, string? Kind, JsonElement? Source, RawQualify? Qualify, RawPromote? Promote, RawUpdate? Update);

    private sealed record RawQualify(QualifyMode? Mode);

    private sealed record RawPromote(string? Rollback, string? Check);

    private sealed record RawUpdate(List<UpdateKind>? Promote);
}
