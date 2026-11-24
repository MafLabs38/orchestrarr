namespace Orchestrarr.Core.Missions;

/// <summary>
/// What is known about one dependency that is a problem (ADR 0048): an advisory (a known flaw, with the version that fixes it or none), an abandoned library (with a replacement that is suggested, or none). Where it comes from (a forge, a
/// scanner, a file of the test bed) is a source; what it says is **data**, read by the lead agent that plans the work and never obeyed.
/// </summary>
public sealed record DependencyFinding(string Kind, string Package, string? Installed, string? FixedIn, string? Severity, string? Summary, string? Replacement)
{
    private const int MaxField = 200;

    /// <summary>The finding on one line of plain text, every field bounded: this is what the lead reads of it.</summary>
    public string Line => string.Join("; ", Parts());

    private IEnumerable<string> Parts()
    {
        var installed = Installed is { } version ? $" (installed {Plain(version)})" : string.Empty;
        yield return $"{Plain(Kind)} on {Plain(Package)}{installed}";
        yield return FixText();
        if (Severity is { Length: > 0 } severity)
        {
            yield return $"severity {Plain(severity)}";
        }
        if (Replacement is { Length: > 0 } replacement)
        {
            yield return $"a replacement is suggested: {Plain(replacement)}";
        }
        if (Summary is { Length: > 0 } summary)
        {
            yield return Plain(summary);
        }
    }

    private string FixText()
    {
        if (!Kind.Equals("advisory", StringComparison.OrdinalIgnoreCase))
        {
            return "no fixed version applies";
        }
        return FixedIn is { Length: > 0 } fix ? $"fixed in {Plain(fix)}" : "no fixed version exists";
    }

    // One line, no marker of a fence, bounded
    private static string Plain(string value)
    {
        var flat = value.ReplaceLineEndings(" ").Replace("<<<", "< < <", StringComparison.Ordinal).Replace(">>>", "> > >", StringComparison.Ordinal).Trim();
        return flat.Length <= MaxField ? flat : string.Concat(flat.AsSpan(0, MaxField), "…");
    }
}

/// <summary>Where the findings about the dependencies of a repository are read from. A source that cannot answer says nothing (an empty list), never a guess.</summary>
public interface IDependencyFindings
{
    Task<IReadOnlyList<DependencyFinding>> ReadAsync(string repository, CancellationToken cancellationToken);
}

/// <summary>The source of a control plane that has none: no detail about a dependency problem.</summary>
public sealed class NoDependencyFindings : IDependencyFindings
{
    public Task<IReadOnlyList<DependencyFinding>> ReadAsync(string repository, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DependencyFinding>>([]);
}
