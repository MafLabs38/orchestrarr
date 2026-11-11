using Orchestrarr.Core.Applications;

namespace Orchestrarr.Host.Decisions;

/// <summary>
/// The descriptions of the applications (ADR 0020), from <c>Applications:File</c> (relative to the content root). Unset: none is described, and
/// every problem says so. A file that is missing or wrong stops the start.
/// </summary>
internal static class ApplicationCatalogFactory
{
    public const string FileKey = "Applications:File";

    public static IApplicationCatalog Create(IConfiguration configuration, string contentRoot)
    {
        var file = configuration[FileKey];
        if (string.IsNullOrWhiteSpace(file))
        {
            return new EmptyApplicationCatalog();
        }
        var path = Path.GetFullPath(file, contentRoot);
        return File.Exists(path)
            ? ApplicationCatalog.Parse(File.ReadAllText(path))
            : throw new InvalidOperationException($"{FileKey} names a file that does not exist: {path}");
    }
}
