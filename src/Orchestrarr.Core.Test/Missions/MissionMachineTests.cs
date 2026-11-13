using Orchestrarr.Core.Missions;

namespace Orchestrarr.Core.Test.Missions;

public sealed class MissionMachineTests
{
    private static readonly LoopPolicy Loop = new(MaxSendBacksPerMission: 2, MaxMandatesPerProblem: 5, MaxTimePerProblem: TimeSpan.FromHours(1));

    private static Mission Proposed() => new("m1", "p1", "shop", World.Staging, "Fix the deprecated call", new MissionFrame(["example/shop"], [".github/**"], "slot-1"), new MissionBudget(20, 3, 50), MissionState.Proposed);

    private static Mission After(Mission mission, params MissionEvent[] events)
    {
        foreach (var next in events)
        {
            mission = MissionMachine.Apply(mission, next, Loop, mandateId: "mandate-1").Mission!;
        }
        return mission;
    }

    [Fact]
    public void A_mission_goes_from_proposed_to_accepted_and_a_granted_complex_one_says_so()
    {
        var done = After(Proposed(), MissionEvent.MandateIssued, MissionEvent.Started, MissionEvent.HandedOver);

        var granted = MissionMachine.Apply(done, MissionEvent.Granted, Loop, complex: true).Mission!;

        granted.State.Should().Be(MissionState.Accepted);
        granted.End.Should().Be(MissionEnd.DoneComplex);
        granted.MandateId.Should().Be("mandate-1");
        granted.IsOver.Should().BeTrue();
    }

    [Fact]
    public void An_event_that_does_not_fit_the_state_is_refused_and_never_ignored()
    {
        var proposed = Proposed();

        var transition = MissionMachine.Apply(proposed, MissionEvent.HandedOver, Loop);

        transition.Allowed.Should().BeFalse();
        transition.Refusal.Should().Contain("HandedOver").And.Contain("Running");
    }

    [Fact]
    public void A_mission_that_is_over_accepts_nothing()
    {
        var accepted = After(Proposed(), MissionEvent.MandateIssued, MissionEvent.Started, MissionEvent.HandedOver, MissionEvent.Granted);

        MissionMachine.Apply(accepted, MissionEvent.Started, Loop).Allowed.Should().BeFalse();
        MissionMachine.Apply(accepted, MissionEvent.Stopped, Loop).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Sent_back_the_mission_runs_again_in_its_world_and_the_send_backs_are_counted()
    {
        var handed = After(Proposed(), MissionEvent.MandateIssued, MissionEvent.Started, MissionEvent.HandedOver);

        var sentBack = MissionMachine.Apply(handed, MissionEvent.SentBack, Loop).Mission!;

        sentBack.State.Should().Be(MissionState.Running);
        sentBack.SendBacks.Should().Be(1);
    }

    [Fact]
    public void A_mission_sent_back_too_many_times_ends_as_failed_instead_of_looping()
    {
        var mission = After(Proposed(), MissionEvent.MandateIssued, MissionEvent.Started);
        for (var i = 0; i < Loop.MaxSendBacksPerMission; i++)
        {
            mission = After(mission, MissionEvent.HandedOver, MissionEvent.SentBack);
        }

        var last = MissionMachine.Apply(After(mission, MissionEvent.HandedOver), MissionEvent.SentBack, Loop).Mission!;

        last.State.Should().Be(MissionState.Ended);
        last.End.Should().Be(MissionEnd.Failed);
        last.EndReason.Should().Contain("too many times");
    }

    [Theory]
    [InlineData(MissionEvent.BudgetExhausted, MissionEnd.Failed)]
    [InlineData(MissionEvent.Stopped, MissionEnd.Stopped)]
    [InlineData(MissionEvent.MandateExpired, MissionEnd.Stopped)]
    public void A_running_mission_ends_cleanly_on_a_budget_a_stop_or_an_expiry(MissionEvent stop, MissionEnd expected)
    {
        var running = After(Proposed(), MissionEvent.MandateIssued, MissionEvent.Started);

        var ended = MissionMachine.Apply(running, stop, Loop).Mission!;

        ended.State.Should().Be(MissionState.Ended);
        ended.End.Should().Be(expected);
        ended.EndReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_mission_with_no_mandate_has_nothing_to_stop()
    {
        MissionMachine.Apply(Proposed(), MissionEvent.Stopped, Loop).Allowed.Should().BeFalse();
    }

    [Fact]
    public void The_controller_can_end_a_mission_that_handed_over_with_its_reason()
    {
        var handed = After(Proposed(), MissionEvent.MandateIssued, MissionEvent.Started, MissionEvent.HandedOver);

        var ended = MissionMachine.Apply(handed, MissionEvent.EndedByController, Loop, reason: "the frame was not kept").Mission!;

        ended.End.Should().Be(MissionEnd.Refused);
        ended.EndReason.Should().Be("the frame was not kept");
    }

    [Fact]
    public void The_loop_of_a_problem_is_bounded_in_mandates_and_in_time()
    {
        Loop.Check(0, TimeSpan.Zero).Should().Be(LoopVerdict.Allowed);
        Loop.Check(Loop.MaxMandatesPerProblem, TimeSpan.Zero).Should().Be(LoopVerdict.TooManyMandates);
        Loop.Check(1, TimeSpan.FromHours(2)).Should().Be(LoopVerdict.TooLong);
    }
}
