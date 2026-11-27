namespace Orchestrarr.Core.Missions;

/// <summary>A problem that the maintainer validated and the lead planned, and that waits for its repository to be free (<see cref="ProblemQueue"/>).</summary>
public sealed record QueuedProblem(string RowId, PlanEntry Entry, DateTimeOffset QueuedAt, int Priority = FlowPriority.Normal, HeldProblem? Held = null);

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
    public bool Waits(string repository, Func<QueuedProblem, bool>? ignoring = null)
    {
        lock (_gate)
        {
            // A problem that waits for its bot (parked) is not ahead of anyone: the lot goes on without it (ADR 0055)
            return _waiting.Exists(other => IsOn(other, repository) && (ignoring is null || !ignoring(other)));
        }
    }

    public IReadOnlyList<QueuedProblem> Waiting()
    {
        lock (_gate)
        {
            return [.. _waiting];
        }
    }

    /// <summary>The line in the order it goes (ADR 0055, ADR 0056): the priority first, then the rank in the route, then arrival. What the dispatch list shows, and what <see cref="TakeReady"/> follows.</summary>
    public IReadOnlyList<QueuedProblem> Ordered()
    {
        lock (_gate)
        {
            return [.. InOrder(_waiting)];
        }
    }

    /// <summary>A person sets the priority of a waiting problem (0 to 100, ADR 0056): it orders the line and grants no power. False when it was not waiting.</summary>
    public bool SetPriority(string problemId, int priority)
    {
        lock (_gate)
        {
            var index = _waiting.FindIndex(other => string.Equals(other.Entry.ProblemId, problemId, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }
            _waiting[index] = _waiting[index] with { Priority = Math.Clamp(priority, FlowPriority.Lowest, FlowPriority.Highest) };
            return true;
        }
    }

    /// <summary>The order in which the line goes (ADR 0055, ADR 0056): the priority, then the rank in the route, then arrival. The screens show the line in this order, the one <see cref="TakeReady"/> follows.</summary>
    public static IOrderedEnumerable<QueuedProblem> InOrder(IEnumerable<QueuedProblem> waiting) =>
        waiting.OrderByDescending(other => other.Priority).ThenBy(other => RouteRank.Of(other.Entry)).ThenBy(other => other.QueuedAt);

    /// <summary>The problems that may start now: the first of each repository that <paramref name="isBusy"/> says is free, in the order of the line (priority, then <see cref="RouteRank"/>, then arrival). They leave the line.</summary>
    public IReadOnlyList<QueuedProblem> TakeReady(Func<string, bool> isBusy, Func<QueuedProblem, bool>? canStart = null)
    {
        ArgumentNullException.ThrowIfNull(isBusy);
        lock (_gate)
        {
            var ready = new List<QueuedProblem>();
            // Whether a repository is busy is asked **once per pass**: the problem before may end while the pass runs, and the route must not see the lot busy for its first operation and free for a later one
            var busy = new Dictionary<string, bool>(StringComparer.Ordinal);
            bool Busy(string repository)
            {
                if (!busy.TryGetValue(repository, out var known))
                {
                    known = isBusy(repository);
                    busy[repository] = known;
                }
                return known;
            }
            foreach (var problem in InOrder(_waiting))
            {
                var repository = RepositoryOf(problem);
                // A problem that cannot start now (it waits for a bot to rebase its pull request) does not hold its lot: the next one of the route takes the turn (ADR 0055)
                // A problem that a person held does not start either, and does not hold its lot (ADR 0056); the hold is read here, under the gate of the line, so a hold and a start never cross
                if (repository is not null && problem.Held is null && !Busy(repository) && !ready.Exists(other => IsOn(other, repository)) && (canStart is null || canStart(problem)))
                {
                    ready.Add(problem);
                }
            }
            _waiting.RemoveAll(ready.Contains);
            return ready;
        }
    }

    /// <summary>A person holds a waiting problem (ADR 0056). False when it is not waiting: one that has started, or left the line, is not held. The hold is part of the line, so it leaves with the problem whatever takes it out.</summary>
    public bool Hold(string problemId, HeldProblem held)
    {
        ArgumentNullException.ThrowIfNull(held);
        return Update(problemId, waiting => waiting with { Held = held });
    }

    /// <summary>A person releases a held problem: it takes its place in the line again. False when it is not waiting or not held.</summary>
    public bool Release(string problemId)
    {
        lock (_gate)
        {
            var index = _waiting.FindIndex(other => string.Equals(other.Entry.ProblemId, problemId, StringComparison.Ordinal));
            if (index < 0 || _waiting[index].Held is null)
            {
                return false;
            }
            _waiting[index] = _waiting[index] with { Held = null };
            return true;
        }
    }

    /// <summary>Who held a waiting problem, or null when it is not waiting or not held.</summary>
    public HeldProblem? HeldOf(string problemId)
    {
        lock (_gate)
        {
            return _waiting.Find(other => string.Equals(other.Entry.ProblemId, problemId, StringComparison.Ordinal))?.Held;
        }
    }

    private bool Update(string problemId, Func<QueuedProblem, QueuedProblem> change)
    {
        lock (_gate)
        {
            var index = _waiting.FindIndex(other => string.Equals(other.Entry.ProblemId, problemId, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }
            _waiting[index] = change(_waiting[index]);
            return true;
        }
    }

    /// <summary>The plan entry of a waiting problem is replaced (the pull request was made again by its bot: the work follows its new head). False when it was not waiting.</summary>
    public bool Replace(string problemId, PlanEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            var index = _waiting.FindIndex(other => string.Equals(other.Entry.ProblemId, problemId, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }
            _waiting[index] = _waiting[index] with { Entry = entry };
            return true;
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
