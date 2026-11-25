using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Machines;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

/// <summary>A mission goes to one lane (ADR 0053): an offer is held for a minute against the other lanes, a lane of a machine gets its own offer again, and a mission never holds two machines.</summary>
public sealed class MissionOffersTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_mission_that_was_offered_is_held_against_the_other_lanes_for_a_minute_and_then_offered_again()
    {
        var time = new FakeTimeProvider(T0);
        var progress = new MissionProgress(time);

        progress.Offered("m1", "staging-1");

        progress.RecentlyOffered("m1", "staging-2").Should().BeTrue("another machine does not get what was offered to the first");
        progress.RecentlyOffered("m1", "staging-1").Should().BeFalse("the lane that was offered it asks again after a restart");
        progress.RecentlyOffered("m1", null).Should().BeTrue("the lanes of the Git world have no machine to tell them apart: none gets it again");
        progress.RecentlyOffered("m2", "staging-2").Should().BeFalse("what was never offered is free");
        time.Advance(MissionProgress.OfferHolds + TimeSpan.FromSeconds(1));
        progress.RecentlyOffered("m1", "staging-2").Should().BeFalse("an offer that nobody started is not held for ever");
        progress.RecentlyOffered("m1", null).Should().BeFalse();
    }

    [Fact]
    public void A_mission_that_holds_a_machine_is_not_given_a_second_one()
    {
        var pool = new MachinePool(new InMemoryMachineStore(), new FakeTimeProvider(T0));
        pool.Register("leipzig", MachineRole.Staging);
        pool.Register("nantes", MachineRole.Staging);
        foreach (var id in new[] { "leipzig", "nantes" })
        {
            pool.BeginQualification(id, "test");
            pool.Report(id, new Qualification(T0, [new MachineCheck("restored", true, ""), new MachineCheck("reachable", true, ""), new MachineCheck("isolation", true, "")]));
        }
        var options = new MissionsOptions { Machines = [new MachineSetting { Id = "leipzig", Role = MachineRole.Staging, Runner = "runner" }, new MachineSetting { Id = "nantes", Role = MachineRole.Staging, Runner = "runner" }] };
        var mission = new Mission("m1", "p1", "shop", World.Staging, "o", new MissionFrame(["o/shop"], [], ""), new MissionBudget(1, 1, 1), MissionState.Mandated);

        MachineEndpoints.TryLeaseFor(pool, options, "leipzig", "runner", mission).Should().Be((true, true));
        MachineEndpoints.TryLeaseFor(pool, options, "nantes", "runner", mission).Should().Be((false, false), "the first lane keeps the machine, the mission is not leased a second one");
        MachineEndpoints.TryLeaseFor(pool, options, "leipzig", "runner", mission).Should().Be((true, false), "the lane that holds it asks again and keeps what it has");
        pool.Get("nantes")!.State.Should().Be(MachineState.Idle);
    }
}
