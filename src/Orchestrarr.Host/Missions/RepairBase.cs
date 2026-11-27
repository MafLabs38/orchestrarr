using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>The text, at the head that is repaired, of the files of a repair that the guards read line by line (ADR 0055): so that a repair is judged on what it adds, not on what the pull request already held.</summary>
internal static class RepairBase
{
    internal static async Task<IReadOnlyDictionary<string, string>> ReadAsync(IBranchSource branches, string repository, string sha, IReadOnlyList<PatchedFile> files, CancellationToken cancellationToken)
    {
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in files.Select(file => file?.Path).OfType<string>().Where(path => RepairPolicy.IsPlainPath(path) && RepairPolicy.ReadsLines(path)).Distinct(StringComparer.Ordinal).Take(RepairPolicy.MaxFiles))
        {
            switch (await branches.FileAtAsync(repository, sha, path, cancellationToken))
            {
                case { Exists: true, Text: { } text }:
                    before[path] = text;
                    break;
                case { Exists: false }:
                    // A file the repair creates: nothing to compare with, it is judged whole
                    break;
                default:
                    // An existing file, or one that could not be told: what the repair removes from it cannot be judged, so it is not passed
                    before[path] = RepairPolicy.Unread;
                    break;
            }
        }
        return before;
    }
}
