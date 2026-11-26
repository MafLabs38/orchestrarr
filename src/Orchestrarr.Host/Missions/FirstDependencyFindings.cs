using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>Several sources of findings in order: the first one that says something about a repository is the one that is read (the lab's file before the forge, on the test bed).</summary>
internal sealed class FirstDependencyFindings(IReadOnlyList<IDependencyFindings> sources) : IDependencyFindings
{
    public async Task<IReadOnlyList<DependencyFinding>> ReadAsync(string repository, CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            if (await source.ReadAsync(repository, cancellationToken) is { Count: > 0 } found)
            {
                return found;
            }
        }
        return [];
    }
}
