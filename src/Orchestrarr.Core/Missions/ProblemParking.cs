using System.Collections.Concurrent;

namespace Orchestrarr.Core.Missions;

/// <summary>A problem that waits for a bot to rebase its pull request: since when, how many times the bot was asked, when it was last read and asked, and why (ADR 0055).</summary>
public sealed record ParkedProblem(DateTimeOffset Since, int Requests, DateTimeOffset LastChecked, DateTimeOffset? LastRequested, string Reason);

/// <summary>
/// The problems of the line that are **parked**: they wait for a bot, and the lot goes on with the next operation of its route meanwhile (ADR 0055). In memory, like the line itself: a restart empties both.
/// </summary>
public sealed class ProblemParking
{
    private readonly ConcurrentDictionary<string, ParkedProblem> _parked = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _requeues = new(StringComparer.Ordinal);

    public ParkedProblem? Get(string problemId) => _parked.TryGetValue(problemId, out var parked) ? parked : null;

    public bool IsParked(string problemId) => _parked.ContainsKey(problemId);

    public void Set(string problemId, ParkedProblem parked) => _parked[problemId] = parked;

    /// <summary>How many times a problem was put back in the line after the runner refused its pull request (a bot made it again while it was proved).</summary>
    public int Requeues(string problemId) => _requeues.GetValueOrDefault(problemId);

    public int AddRequeue(string problemId) => _requeues.AddOrUpdate(problemId, 1, (_, count) => count + 1);

    /// <summary>The problem is no longer parked (it starts, it left the line, or the bot did its work). False when it was not.</summary>
    public bool Release(string problemId) => _parked.TryRemove(problemId, out _);
}
