namespace Orchestrarr.Core.Machines;

/// <summary>What a request on a machine gave: the machine as it is now, or why nothing was done.</summary>
public sealed record MachineResult(bool Ok, Machine? Machine, string? Refusal)
{
    public static MachineResult Done(Machine machine) => new(true, machine, null);

    public static MachineResult Refused(Machine? machine, string why) => new(false, machine, why);
}

/// <summary>
/// The pool of lab machines (ADR 0047): the state of each, who holds it, and the only transitions there are. **One holder at a time**: a lease is taken on an <see cref="MachineState.Idle"/> machine only, and what a job leaves
/// is never given to the next one before a qualification. Every change is written with its transition, and announced.
/// </summary>
public sealed class MachinePool(IMachineStore store, TimeProvider time, MachineOptions? options = null)
{
    private readonly object _gate = new();
    private readonly MachineOptions _options = options ?? MachineOptions.Default;

    /// <summary>A machine changed: its new state (announced to the screens).</summary>
    public event Action<Machine>? Changed;

    public IReadOnlyList<Machine> All() => store.All();

    public Machine? Get(string id) => store.Get(id);

    public IReadOnlyList<MachineTransition> Transitions(string id) => store.Transitions(id);

    /// <summary>At the start of the control plane: a machine it knows is put to qualify, because a machine is Idle only after a qualification **by this control plane**; one it does not know is created.</summary>
    public Machine Register(string id, MachineRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Machine changed;
        lock (_gate)
        {
            var known = store.Get(id);
            var at = time.GetUtcNow();
            var next = new Machine(id, role, MachineState.Qualifying, at, null, "the control plane started: the machine must be qualified");
            // A machine that was in process when the control plane stopped is finished, and no mission holds it any more: it goes through a qualification all the same
            if (known is { State: MachineState.Qualifying })
            {
                changed = known;
            }
            else
            {
                MachineState? via = known?.State == MachineState.InProcess ? MachineState.Finished : null;
                changed = Write(known, next, via);
            }
        }
        Changed?.Invoke(changed);
        return changed;
    }

    /// <summary>A mission takes an Idle machine. Anything else is refused: the mission waits.</summary>
    public MachineResult TryLease(string id, string missionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(missionId);
        MachineResult result;
        lock (_gate)
        {
            if (store.Get(id) is not { } machine)
            {
                return MachineResult.Refused(null, "no such machine");
            }
            if (machine.State == MachineState.InProcess && machine.MissionId == missionId)
            {
                // The same mission asks again (a claim that was repeated): it keeps what it has
                result = MachineResult.Done(machine);
            }
            else if (machine.State != MachineState.Idle)
            {
                return MachineResult.Refused(machine, $"the machine is {machine.State}, not Idle");
            }
            else
            {
                var at = time.GetUtcNow();
                result = MachineResult.Done(Write(machine, machine with { State = MachineState.InProcess, Since = at, MissionId = missionId, Reason = null, LeaseRenewedAt = at }));
            }
        }
        Changed?.Invoke(result.Machine!);
        return result;
    }

    /// <summary>A sign of life of the runner that holds the machine: the lease lives on.</summary>
    public bool Heartbeat(string id, string missionId)
    {
        lock (_gate)
        {
            if (store.Get(id) is not { State: MachineState.InProcess } machine || machine.MissionId != missionId)
            {
                return false;
            }
            // A renewal is not a change of state: it is kept on the machine and not written as a transition
            store.Save(machine with { LeaseRenewedAt = time.GetUtcNow() }, null);
            return true;
        }
    }

    /// <summary>The mission is over, in any way: the machine is Finished and cannot take another job before it is qualified. Only the holder gives it back.</summary>
    public MachineResult Release(string id, string missionId, string? reason = null)
    {
        MachineResult result;
        lock (_gate)
        {
            if (store.Get(id) is not { } machine)
            {
                return MachineResult.Refused(null, "no such machine");
            }
            if (machine.State != MachineState.InProcess || machine.MissionId != missionId)
            {
                return MachineResult.Refused(machine, "the machine is not held by this mission");
            }
            result = MachineResult.Done(Write(machine, machine with { State = MachineState.Finished, Since = time.GetUtcNow(), Reason = reason, LeaseRenewedAt = null }));
        }
        Changed?.Invoke(result.Machine!);
        return result;
    }

    /// <summary>A machine that is Finished, Down or Idle goes to be qualified (an Idle one only when it is asked: it is then not given to anyone meanwhile).</summary>
    public MachineResult BeginQualification(string id, string? reason = null)
    {
        MachineResult result;
        lock (_gate)
        {
            if (store.Get(id) is not { } machine)
            {
                return MachineResult.Refused(null, "no such machine");
            }
            if (machine.State == MachineState.Qualifying)
            {
                return MachineResult.Done(machine);
            }
            if (!MachineRules.Allows(machine.State, MachineState.Qualifying))
            {
                return MachineResult.Refused(machine, $"a machine that is {machine.State} is not qualified");
            }
            result = MachineResult.Done(Write(machine, machine with { State = MachineState.Qualifying, Since = time.GetUtcNow(), MissionId = null, Reason = reason }));
        }
        Changed?.Invoke(result.Machine!);
        return result;
    }

    /// <summary>The result of a qualification: Idle if every check passed, Down with the reason if one failed. Only a machine that is being qualified takes it.</summary>
    public MachineResult Report(string id, Qualification qualification)
    {
        ArgumentNullException.ThrowIfNull(qualification);
        MachineResult result;
        lock (_gate)
        {
            if (store.Get(id) is not { } machine)
            {
                return MachineResult.Refused(null, "no such machine");
            }
            if (machine.State != MachineState.Qualifying)
            {
                return MachineResult.Refused(machine, $"the machine is {machine.State}, it is not being qualified");
            }
            var passed = qualification.Passed;
            string? reason = null;
            if (!passed)
            {
                reason = qualification.Checks.Count == 0 ? "the qualification made no check" : qualification.Failures;
            }
            var next = machine with { State = passed ? MachineState.Idle : MachineState.Down, Since = time.GetUtcNow(), Reason = reason, LastQualification = qualification };
            result = MachineResult.Done(Write(machine, next));
        }
        Changed?.Invoke(result.Machine!);
        return result;
    }

    /// <summary>A machine that cannot be trusted any more (a check made while it is Idle or held failed): it is Down, with why.</summary>
    public MachineResult MarkDown(string id, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        MachineResult result;
        lock (_gate)
        {
            if (store.Get(id) is not { } machine)
            {
                return MachineResult.Refused(null, "no such machine");
            }
            if (!MachineRules.Allows(machine.State, MachineState.Down))
            {
                return MachineResult.Refused(machine, $"a machine that is {machine.State} cannot be put down");
            }
            result = MachineResult.Done(Write(machine, machine with { State = MachineState.Down, Since = time.GetUtcNow(), MissionId = null, Reason = reason, LeaseRenewedAt = null }));
        }
        Changed?.Invoke(result.Machine!);
        return result;
    }

    /// <summary>The leases whose runner has given no sign of life for too long end: the machine is Finished, so that it is restored, and the lease cannot be kept for ever by a runner that has gone.</summary>
    public IReadOnlyList<Machine> ExpireLeases()
    {
        var expired = new List<Machine>();
        lock (_gate)
        {
            var now = time.GetUtcNow();
            foreach (var machine in store.All().Where(candidate => candidate.State == MachineState.InProcess && now - (candidate.LeaseRenewedAt ?? candidate.Since) > _options.LeaseTimeout))
            {
                expired.Add(Write(machine, machine with { State = MachineState.Finished, Since = now, Reason = "the lease expired: its runner gave no sign of life", LeaseRenewedAt = null }));
            }
        }
        foreach (var machine in expired)
        {
            Changed?.Invoke(machine);
        }
        return expired;
    }

    // Writes a new state with its transition; the caller holds the gate. <paramref name="viaFinished"/>: a machine that was in process is first recorded as finished, so that the time in process ends where it ended
    private Machine Write(Machine? before, Machine after, MachineState? viaFinished = null)
    {
        if (before is not null && viaFinished is { } intermediate)
        {
            store.Save(before with { State = intermediate, Since = after.Since, MissionId = null }, new MachineTransition(before.Id, before.State, intermediate, after.Since, before.MissionId, "the control plane started while the machine was in process"));
            before = before with { State = intermediate };
        }
        if (!MachineRules.Allows(before?.State, after.State))
        {
            throw new InvalidOperationException($"a machine does not go from {before?.State.ToString() ?? "unknown"} to {after.State}");
        }
        store.Save(after, new MachineTransition(after.Id, before?.State, after.State, after.Since, after.MissionId ?? before?.MissionId, after.Reason));
        return after;
    }
}
