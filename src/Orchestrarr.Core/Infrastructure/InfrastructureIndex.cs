namespace Orchestrarr.Core.Infrastructure;

/// <summary>A repository of the description where versions are written: which kind of files, which paths (globs) are looked at, and the environment it deploys to when it is one. Nothing else is read (ADR 0028).</summary>
public sealed record RepositoryScope(string Repository, string Kind, IReadOnlyList<string> Paths, string? Environment = null);

/// <summary>A file read from a repository of the scope.</summary>
public sealed record InfrastructureFile(string Repository, string Path, string Kind, string Content, string? Environment = null);

/// <summary>A repository of the scope that could not be read, and why in a word: the table says so instead of showing nothing.</summary>
public sealed record RepositoryProblem(string Repository, string Reason);

/// <summary>What the repositories of the scope say, at one moment.</summary>
public sealed record InfrastructureSnapshot(DateTimeOffset ReadAt, IReadOnlyList<InfrastructureFile> Files, IReadOnlyList<RepositoryProblem> Problems)
{
    public static InfrastructureSnapshot Empty { get; } = new(DateTimeOffset.MinValue, [], []);
}

/// <summary>Reads the files of the repositories of the scope, read-only. The provider (GitHub) is the only one that talks to a forge.</summary>
public interface IInfrastructureSource
{
    /// <summary>Reads the files of the repositories of the scope. A repository that cannot be read is a problem of the snapshot, not a failure of the call.</summary>
    Task<InfrastructureSnapshot> ReadAsync(IReadOnlyList<RepositoryScope> scope, CancellationToken cancellationToken);
}

public sealed class NoInfrastructureSource : IInfrastructureSource
{
    public Task<InfrastructureSnapshot> ReadAsync(IReadOnlyList<RepositoryScope> scope, CancellationToken cancellationToken) => Task.FromResult(InfrastructureSnapshot.Empty);
}

/// <summary>
/// Where each image is written in the code of the scope (ADR 0028). Built from a snapshot, it answers by the name of the image; the files it was built from
/// stay with it so that a change can be shown from the very text that was read.
/// </summary>
public sealed class InfrastructureIndex
{
    private readonly ILookup<string, CodeLocation> _byName;
    private readonly Dictionary<(string Repository, string Path), string> _files;

    private InfrastructureIndex(IReadOnlyList<CodeLocation> locations, Dictionary<(string, string), string> files, IReadOnlyList<RepositoryProblem> problems, bool inScope)
    {
        Locations = locations;
        Problems = problems;
        InScope = inScope;
        _files = files;
        _byName = locations.ToLookup(location => location.Image.Name, StringComparer.Ordinal);
    }

    public static InfrastructureIndex Empty { get; } = new([], [], [], inScope: false);

    public IReadOnlyList<CodeLocation> Locations { get; }

    public IReadOnlyList<RepositoryProblem> Problems { get; }

    /// <summary>How many files were read.</summary>
    public int FileCount => _files.Count;

    /// <summary>True when the description names at least one repository: without one, "found nowhere" would mean nothing.</summary>
    public bool InScope { get; }

    public static InfrastructureIndex Build(InfrastructureSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var locations = new List<CodeLocation>();
        var files = new Dictionary<(string, string), string>();
        foreach (var file in snapshot.Files)
        {
            files[(file.Repository, file.Path)] = file.Content;
            locations.AddRange(VersionParsers.Find(file.Kind, file.Repository, file.Path, file.Content).Select(location => location with { Environment = file.Environment }));
        }
        return new InfrastructureIndex(locations, files, snapshot.Problems, inScope: snapshot.Files.Count > 0 || snapshot.Problems.Count > 0);
    }

    /// <summary>The places that write this image, in the order of the files.</summary>
    public IReadOnlyList<CodeLocation> Find(ImageReference image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return [.. _byName[image.Name]];
    }

    /// <summary>The text of a file as it was read, or null when it is not in the snapshot.</summary>
    public string? Content(string repository, string path) => _files.GetValueOrDefault((repository, path));
}
