namespace Orchestrarr.Core.Missions;

/// <summary>Who held an operation, when, and why (ADR 0056).</summary>
public sealed record HeldProblem(string By, DateTimeOffset At, string Reason);

/// <summary>
/// The holds that a person puts on operations that have not started (ADR 0056, the MES's *hold*): a held operation does not start until it is released, and **it never holds its lot**: the next operation of the route takes the turn.
/// A hold only delays work, it grants nothing. It is **part of the line** (<see cref="ProblemQueue"/>): set and read under the line's gate, it leaves with the operation whatever takes it out (a start, the global stop, the end of
/// the approval's lifetime, a bot's pull request that left), so a row validated again is never held by an earlier hold. In memory, like the line: a restart empties both.
/// </summary>
public sealed class ProblemHolds(ProblemQueue line)
{
    public const int MaxReason = 300;

    public HeldProblem? Get(string problemId) => line.HeldOf(problemId);

    public bool IsHeld(string problemId) => line.HeldOf(problemId) is not null;

    /// <summary>False when the operation is not waiting in the line (it started, or left it): nothing is held then.</summary>
    public bool Hold(string problemId, HeldProblem held) => line.Hold(problemId, held);

    public bool Release(string problemId) => line.Release(problemId);
}
