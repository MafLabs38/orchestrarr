using System.Text.Json;
using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Infrastructure;
using YamlDotNet.Serialization;

namespace Orchestrarr.Host.Decisions;

/// <summary>What the description file says: the descriptions of the applications (ADR 0020) and the perimeter of the work (ADR 0028).</summary>
internal sealed record DescriptionContent(IApplicationCatalog Applications, DescriptionScope Scope);

/// <summary>
/// The description, from <c>Description:File</c> (relative to the content root; <c>Applications:File</c> is still read). YAML or JSON, which is a form of YAML: one file
/// is the single entry that says what Orchestrarr maintains. Unset: nothing is described and nothing is in the scope. A file that is missing or wrong stops the start.
/// </summary>
internal static class DescriptionFile
{
    public const string FileKey = "Description:File";
    public const string LegacyFileKey = ApplicationCatalogFactory.FileKey;

    public static DescriptionContent Load(IConfiguration configuration, string contentRoot)
    {
        var file = configuration[FileKey] is { Length: > 0 } named ? named : configuration[LegacyFileKey];
        if (string.IsNullOrWhiteSpace(file))
        {
            return new DescriptionContent(new EmptyApplicationCatalog(), DescriptionScope.None);
        }
        var path = Path.GetFullPath(file, contentRoot);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{FileKey} names a file that does not exist: {path}");
        }
        var json = ToJson(File.ReadAllText(path));
        return new DescriptionContent(ApplicationCatalog.Parse(json), DescriptionScope.Parse(json));
    }

    /// <summary>The same document as JSON, whatever it was written in: YAML maps become objects, lists arrays, scalars text, numbers and booleans.</summary>
    public static string ToJson(string text)
    {
        object? tree;
        try
        {
            tree = new DeserializerBuilder().Build().Deserialize<object?>(text);
        }
        catch (YamlDotNet.Core.YamlException failure)
        {
            throw new InvalidOperationException($"The description cannot be read (line {failure.Start.Line}): {failure.Message}", failure);
        }
        return JsonSerializer.Serialize(Plain(tree));
    }

    private static object? Plain(object? value) => value switch
    {
        IDictionary<object, object> map => map.ToDictionary(pair => Convert.ToString(pair.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, pair => Plain(pair.Value), StringComparer.Ordinal),
        IEnumerable<object> list when value is not string => list.Select(Plain).ToList(),
        string text => Scalar(text),
        _ => value,
    };

    // YamlDotNet reads every scalar as text: true, false and numbers are given back their type, so that a JSON reader sees what a YAML one would
    private static object Scalar(string text) => text switch
    {
        "true" => true,
        "false" => false,
        _ when long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) && (text == "0" || !text.StartsWith('0')) => number,
        _ => text,
    };
}
