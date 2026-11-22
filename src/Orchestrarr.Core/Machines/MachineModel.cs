namespace Orchestrarr.Core.Machines;

/// <summary>What a lab machine is for: the staging worlds work in one that is restored after each job; the release world deploys on one that keeps what it runs.</summary>
public enum MachineRole
{
    Staging,
    Release,
}

/// <summary>
/// The state of a lab machine, as for equipment in a clean room (ADR 0047). Only a machine that is <see cref="Idle"/> can be given a job; it is Idle only after a qualification.
/// </summary>
public enum MachineState
{
    /// <summary>Being restored and checked: not given to anyone.</summary>
    Qualifying,

    /// <summary>Qualified and free: a job may take it.</summary>
    Idle,

    /// <summary>A mission holds it (a lease): no other mission is given it.</summary>
    InProcess,

    /// <summary>The job is over and the machine is not qualified any more: it must be restored and checked before the next job.</summary>
    Finished,

    /// <summary>Not viable: a qualification failed or it cannot be reached. The reason is kept; it waits for a repair, then a new qualification.</summary>
    Down,
}

/// <summary>One check of a qualification: its name, whether it passed, and what was seen.</summary>
public sealed record MachineCheck(string Name, bool Passed, string Detail);

/// <summary>The result of a qualification: the checks made, and when. It passes only if there is at least one check and every check passed.</summary>
public sealed record Qualification(DateTimeOffset At, IReadOnlyList<MachineCheck> Checks)
{
    public bool Passed => Checks.Count > 0 && Checks.All(check => check.Passed);

    /// <summary>Why it did not pass, in one line (empty when it passed).</summary>
    public string Failures => string.Join("; ", Checks.Where(check => !check.Passed).Select(check => $"{check.Name}: {check.Detail}"));
}

/// <summary>A lab machine, its state, since when, who holds it, why it is down, and the last qualification.</summary>
public sealed record Machine(
    string Id,
    MachineRole Role,
    MachineState State,
    DateTimeOffset Since,
    string? MissionId = null,
    string? Reason = null,
    DateTimeOffset? LeaseRenewedAt = null,
    Qualification? LastQualification = null);

/// <summary>One change of state of a machine, kept for ever: from it the time up, down and in process is computed.</summary>
public sealed record MachineTransition(string MachineId, MachineState? From, MachineState To, DateTimeOffset At, string? MissionId, string? Reason);

/// <summary>The transitions that exist (ADR 0047): nothing else changes the state of a machine.</summary>
public static class MachineRules
{
    /// <summary>
    /// The checks without which no machine is qualified, whatever else passed: a machine is reachable, **isolated**, and a staging machine has been restored. A report that lacks one, or in which one failed, makes the machine Down:
    /// the control plane does not take the word "qualified" of a runner that did not make the checks.
    /// </summary>
    public static IReadOnlyList<string> RequiredChecks(MachineRole role) => role == MachineRole.Staging ? ["restored", "reachable", "isolation"] : ["reachable", "isolation"];

    public static bool Allows(MachineState? from, MachineState to) => (from, to) switch
    {
        (null, MachineState.Qualifying) => true,
        (MachineState.Qualifying, MachineState.Idle or MachineState.Down) => true,
        (MachineState.Idle, MachineState.InProcess or MachineState.Qualifying or MachineState.Down) => true,
        (MachineState.InProcess, MachineState.Finished or MachineState.Down) => true,
        (MachineState.Finished, MachineState.Qualifying or MachineState.Down) => true,
        (MachineState.Down, MachineState.Qualifying) => true,
        _ => false,
    };
}

/// <summary>Where the machines and their transitions are kept (in memory in a test, in the SQLite file of the control plane).</summary>
public interface IMachineStore
{
    Machine? Get(string id);

    IReadOnlyList<Machine> All();

    /// <summary>Writes the new state and, with it, the transition: both or none.</summary>
    void Save(Machine machine, MachineTransition? transition);

    /// <summary>The transitions of a machine, oldest first.</summary>
    IReadOnlyList<MachineTransition> Transitions(string id);

    /// <summary>The transitions from <paramref name="since"/> on, **and the last one before it** (the state at the start of the window): what the statistics of a window need, without reading the history of the machine for ever.</summary>
    IReadOnlyList<MachineTransition> Transitions(string id, DateTimeOffset since);
}

/// <summary>How long a lease lives without a sign of life of its runner. Generous: a long troubleshooting is normal, only a runner that has disappeared must not keep a machine for ever.</summary>
public sealed record MachineOptions(TimeSpan LeaseTimeout)
{
    public static MachineOptions Default { get; } = new(TimeSpan.FromMinutes(30));
}
