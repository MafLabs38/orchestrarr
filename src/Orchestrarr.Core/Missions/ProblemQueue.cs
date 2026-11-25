namespace Orchestrarr.Core.Missions;

/// <summary>A problem that the maintainer validated and the lead planned, and that waits for its repository to be free (<see cref="ProblemQueue"/>).</summary>
public sealed record QueuedProblem(string RowId, PlanEntry Entry, DateTimeOffset QueuedAt);

/// <summary>
/// **One work site at a time on a repository** (the maintainer's Git flow rule, 2026-11-24): problems that are validated together on one repository are done one after the other, so that what is proved in staging is what is
/// merged on a base that nobody else moved, and no two changes of the same repository can conflict because of this product. A problem waits here, planned but not started (no mandate is issued, no approval is used), and starts when
/// no other problem is in progress on its repository. A problem that fails does not hold the others: it ends, and the next one starts (a release that did not verify is not a failure of the problem: it goes back and keeps its repository, ADR 0050). In memory, like the other stores of the test bed: a restart empties it, and the maintainer validates again.
/// </summary>
public sealed class ProblemQueue
{
    private readonly object _gate = new();
    private readonly List<QueuedProblem> _waiting = [];

    /// <summary>Puts a problem at the end of the line of its repository. Returns false when the same problem already waits.</summary>
    public bool Enqueue(QueuedProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        lock (_gate)
        {
            if (_waiting.Exists(other => string.Equals(other.Entry.ProblemId, problem.Entry.ProblemId, StringComparison.Ordinal)))
            {
                return false;
            }
            _waiting.Add(problem);
            return true;
        }
    }

    public bool Contains(string problemId)
    {
        lock (_gate)
        {
            return _waiting.Exists(other => string.Equals(other.Entry.ProblemId, problemId, StringComparison.Ordinal));
        }
    }

    /// <summary>Whether a problem of this repository waits: a new problem on it goes behind it, even if the repository is free at this moment.</summary>
    public bool Waits(string repository)
    {
        lock (_gate)
        {
            return _waiting.Exists(other => IsOn(other, repository));
        }
    }

    public IReadOnlyList<QueuedProblem> Waiting()
    {
        lock (_gate)
        {
            return [.. _waiting];
        }
    }

    /// <summary>The problems that may start now: the first of each repository that <paramref name="isBusy"/> says is free, in the order of the route (<see cref="RouteRank"/>, then arrival). They leave the line.</summary>
    public IReadOnlyList<QueuedProblem> TakeReady(Func<string, bool> isBusy)
    {
        ArgumentNullException.ThrowIfNull(isBusy);
        lock (_gate)
        {
            var ready = new List<QueuedProblem>();
            foreach (var problem in _waiting.OrderBy(other => RouteRank.Of(other.Entry)).ThenBy(other => other.QueuedAt))
            {
                var repository = RepositoryOf(problem);
                if (repository is not null && !isBusy(repository) && !ready.Exists(other => IsOn(other, repository)))
                {
                    ready.Add(problem);
                }
            }
            _waiting.RemoveAll(ready.Contains);
            return ready;
        }
    }

    /// <summary>A problem leaves the line (it waited too long). False when it was not there.</summary>
    public bool Remove(string problemId)
    {
        lock (_gate)
        {
            return _waiting.RemoveAll(other => string.Equals(other.Entry.ProblemId, problemId, StringComparison.Ordinal)) > 0;
        }
    }

    /// <summary>The line is emptied (the global stop): the problems that left it are returned, so that each can be written in the trail.</summary>
    public IReadOnlyList<QueuedProblem> Clear()
    {
        lock (_gate)
        {
            var all = _waiting.ToList();
            _waiting.Clear();
            return all;
        }
    }

    private static string? RepositoryOf(QueuedProblem problem) => problem.Entry.Repositories.Count == 1 ? problem.Entry.Repositories[0] : null;

    private static bool IsOn(QueuedProblem problem, string repository) => string.Equals(RepositoryOf(problem), repository, StringComparison.Ordinal);
}
