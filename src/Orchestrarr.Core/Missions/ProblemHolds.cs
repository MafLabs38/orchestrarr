using System.Collections.Concurrent;

namespace Orchestrarr.Core.Missions;

/// <summary>Who held an operation, when, and why (ADR 0056).</summary>
public sealed record HeldProblem(string By, DateTimeOffset At, string Reason);

/// <summary>
/// The holds that a person put on operations that have not started (ADR 0056, the MES's *hold*): a held operation does not start until it is released, and **it never holds its lot**: the next operation of the route takes the turn.
/// A hold only delays work, it grants nothing. In memory, like the line it is about: a restart empties both.
/// </summary>
public sealed class ProblemHolds
{
    public const int MaxReason = 300;

    private readonly ConcurrentDictionary<string, HeldProblem> _held = new(StringComparer.Ordinal);

    public HeldProblem? Get(string problemId) => _held.TryGetValue(problemId, out var held) ? held : null;

    public bool IsHeld(string problemId) => _held.ContainsKey(problemId);

    public void Hold(string problemId, HeldProblem held) => _held[problemId] = held;

    public bool Release(string problemId) => _held.TryRemove(problemId, out _);
}
