using Orchestrarr.Core.Machines;

namespace Orchestrarr.Storage.Test;

public sealed class SqliteMachineStoreTests : IDisposable
{
    private readonly TempDatabase _file = new();
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    public void Dispose() => _file.Dispose();

    [Fact]
    public void A_machine_with_its_last_qualification_and_its_history_survive_a_restart()
    {
        var qualification = new Qualification(T0, [new MachineCheck("isolation", true, "closed"), new MachineCheck("registries", true, "open")]);
        var machine = new Machine("leipzig", MachineRole.Staging, MachineState.Idle, T0, LastQualification: qualification);
        var store = new SqliteMachineStore(_file.Open());
        store.Save(machine with { State = MachineState.Qualifying }, new MachineTransition("leipzig", null, MachineState.Qualifying, T0.AddMinutes(-2), null, "started"));
        store.Save(machine, new MachineTransition("leipzig", MachineState.Qualifying, MachineState.Idle, T0, null, null));

        var reopened = new SqliteMachineStore(_file.Reopen());

        reopened.Get("leipzig").Should().BeEquivalentTo(machine);
        reopened.Get("leipzig")!.LastQualification!.Passed.Should().BeTrue();
        reopened.All().Should().ContainSingle();
        reopened.Transitions("leipzig").Select(transition => transition.To).Should().Equal(MachineState.Qualifying, MachineState.Idle);
        reopened.Get("bilbao").Should().BeNull();
    }

    [Fact]
    public void A_renewal_of_a_lease_is_saved_without_a_transition_and_the_pool_runs_on_the_file()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(T0);
        var pool = new MachinePool(new SqliteMachineStore(_file.Open()), time);
        pool.Register("leipzig", MachineRole.Staging);
        pool.Report("leipzig", new Qualification(T0, [new MachineCheck("restored", true, "back"), new MachineCheck("reachable", true, "ssh"), new MachineCheck("isolation", true, "closed")]));
        pool.TryLease("leipzig", "m1");
        time.Advance(TimeSpan.FromMinutes(5));
        pool.Heartbeat("leipzig", "m1").Should().BeTrue();

        var restarted = new SqliteMachineStore(_file.Reopen());

        restarted.Get("leipzig")!.State.Should().Be(MachineState.InProcess);
        restarted.Get("leipzig")!.LeaseRenewedAt.Should().Be(T0.AddMinutes(5));
        restarted.Transitions("leipzig").Should().HaveCount(3, "qualifying, idle and in process: a renewal is no transition");
    }
}
