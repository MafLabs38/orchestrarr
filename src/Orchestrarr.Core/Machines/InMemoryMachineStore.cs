namespace Orchestrarr.Core.Machines;

/// <summary>The machines in memory: for tests, and for a control plane without the SQLite file.</summary>
public sealed class InMemoryMachineStore : IMachineStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Machine> _machines = new(StringComparer.Ordinal);
    private readonly List<MachineTransition> _transitions = [];

    public Machine? Get(string id)
    {
        lock (_gate)
        {
            return _machines.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<Machine> All()
    {
        lock (_gate)
        {
            return [.. _machines.Values.OrderBy(machine => machine.Id, StringComparer.Ordinal)];
        }
    }

    public void Save(Machine machine, MachineTransition? transition)
    {
        ArgumentNullException.ThrowIfNull(machine);
        lock (_gate)
        {
            _machines[machine.Id] = machine;
            if (transition is not null)
            {
                _transitions.Add(transition);
            }
        }
    }

    public IReadOnlyList<MachineTransition> Transitions(string id)
    {
        lock (_gate)
        {
            return [.. _transitions.Where(transition => transition.MachineId == id)];
        }
    }
}
