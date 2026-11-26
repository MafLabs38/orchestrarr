using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Machines;

namespace Orchestrarr.Core.Test.Machines;

public sealed class MachinePoolTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));
    private readonly InMemoryMachineStore _store = new();
    private readonly List<Machine> _announced = [];

    private MachinePool Pool(MachineOptions? options = null)
    {
        var pool = new MachinePool(_store, _time, options);
        pool.Changed += _announced.Add;
        return pool;
    }

    private static Qualification Passing(DateTimeOffset at) => new(at, [new MachineCheck("restored", true, "back"), new MachineCheck("reachable", true, "ssh"), new MachineCheck("isolation", true, "closed"), new MachineCheck("routes", true, "open")]);

    private static Qualification Failing(DateTimeOffset at) => new(at, [new MachineCheck("isolation", false, "the control plane answers on 5180"), new MachineCheck("registries", true, "open")]);

    private MachinePool Qualified(string id = "leipzig")
    {
        var pool = Pool();
        pool.Register(id, MachineRole.Staging);
        pool.Report(id, Passing(_time.GetUtcNow()));
        return pool;
    }

    [Fact]
    public void A_machine_is_never_idle_before_a_qualification_and_a_known_machine_goes_through_one_again_at_start()
    {
        var pool = Pool();

        pool.Register("leipzig", MachineRole.Staging).State.Should().Be(MachineState.Qualifying);
        pool.TryLease("leipzig", "m1").Ok.Should().BeFalse("a machine that is not qualified is given to nobody");
        pool.Report("leipzig", Passing(_time.GetUtcNow())).Machine!.State.Should().Be(MachineState.Idle);

        var restarted = Pool();
        restarted.Register("leipzig", MachineRole.Staging).State.Should().Be(MachineState.Qualifying, "a machine is Idle only after a qualification by this control plane");
    }

    [Fact]
    public void One_mission_holds_a_machine_and_the_next_waits_until_the_machine_is_qualified_again()
    {
        var pool = Qualified();

        pool.TryLease("leipzig", "m1").Ok.Should().BeTrue();
        var second = pool.TryLease("leipzig", "m2");
        second.Ok.Should().BeFalse();
        second.Refusal.Should().Contain("InProcess");
        pool.TryLease("leipzig", "m1").Ok.Should().BeTrue("the same mission asking again keeps what it has");

        pool.Release("leipzig", "m1").Machine!.State.Should().Be(MachineState.Finished);
        pool.TryLease("leipzig", "m2").Ok.Should().BeFalse("what a job left is not given to the next one");
        pool.BeginQualification("leipzig").Machine!.State.Should().Be(MachineState.Qualifying);
        pool.Report("leipzig", Passing(_time.GetUtcNow()));
        pool.TryLease("leipzig", "m2").Ok.Should().BeTrue();
    }

    [Fact]
    public void A_lease_says_whether_it_was_just_taken_and_a_repeated_claim_of_the_holder_takes_nothing_new()
    {
        var pool = Qualified();

        pool.TryLease("leipzig", "m1").Taken.Should().BeTrue();
        pool.TryLease("leipzig", "m1").Taken.Should().BeFalse("the holder asking again keeps what it has: a release for that claim would take a machine that is being worked on");
    }

    [Fact]
    public void Only_the_holder_gives_a_machine_back()
    {
        var pool = Qualified();
        pool.TryLease("leipzig", "m1");

        pool.Release("leipzig", "m2").Ok.Should().BeFalse();
        pool.Release("leipzig", "m1").Ok.Should().BeTrue();
        pool.Release("leipzig", "m1").Ok.Should().BeFalse("a machine that is Finished is held by nobody");
    }

    [Fact]
    public void A_qualification_that_fails_makes_the_machine_down_with_why_and_a_machine_that_is_down_is_given_to_nobody()
    {
        var pool = Pool();
        pool.Register("leipzig", MachineRole.Staging);

        var down = pool.Report("leipzig", Failing(_time.GetUtcNow())).Machine!;

        down.State.Should().Be(MachineState.Down);
        down.Reason.Should().Contain("isolation").And.Contain("5180");
        pool.TryLease("leipzig", "m1").Ok.Should().BeFalse();
        pool.BeginQualification("leipzig").Machine!.State.Should().Be(MachineState.Qualifying, "a repair is followed by a new qualification");
        pool.Report("leipzig", Passing(_time.GetUtcNow())).Machine!.State.Should().Be(MachineState.Idle);
    }

    [Fact]
    public void A_machine_that_went_down_is_qualified_again_by_itself_after_a_while_a_bounded_number_of_times_an_hour()
    {
        var pool = Pool();
        pool.Register("bilbao", MachineRole.Release);
        _ = pool.Report("bilbao", Failing(_time.GetUtcNow()));

        pool.RequalifyAfterFault(TimeSpan.FromMinutes(5), 2).Should().BeEmpty("not before the delay");
        for (var round = 0; round < 2; round++)
        {
            _time.Advance(TimeSpan.FromMinutes(6));
            pool.RequalifyAfterFault(TimeSpan.FromMinutes(5), 2).Should().ContainSingle().Which.Reason.Should().StartWith(MachinePool.AutomaticRequalification);
            _ = pool.Report("bilbao", Failing(_time.GetUtcNow()));
        }
        _time.Advance(TimeSpan.FromMinutes(6));

        pool.RequalifyAfterFault(TimeSpan.FromMinutes(5), 2).Should().BeEmpty("a machine that keeps failing stays down for a person");
        pool.Get("bilbao")!.State.Should().Be(MachineState.Down);
    }

    [Theory]
    [InlineData("isolation")]
    [InlineData("reachable")]
    [InlineData("restored")]
    public void A_report_that_lacks_a_check_the_control_plane_requires_does_not_qualify_a_staging_machine_whatever_else_passed(string missing)
    {
        var pool = Pool();
        pool.Register("leipzig", MachineRole.Staging);
        var checks = new[] { "restored", "reachable", "isolation", "routes" }.Where(name => name != missing).Select(name => new MachineCheck(name, true, "ok")).ToList();

        var down = pool.Report("leipzig", new Qualification(_time.GetUtcNow(), checks)).Machine!;

        down.State.Should().Be(MachineState.Down);
        down.Reason.Should().Contain("lacks a passing check").And.Contain(missing);
    }

    [Fact]
    public void A_release_machine_needs_no_restore_but_the_same_isolation()
    {
        var pool = Pool();
        pool.Register("bilbao", MachineRole.Release);

        var idle = pool.Report("bilbao", new Qualification(_time.GetUtcNow(), [new MachineCheck("reachable", true, "ok"), new MachineCheck("isolation", true, "closed")])).Machine!;
        pool.BeginQualification("bilbao");
        var down = pool.Report("bilbao", new Qualification(_time.GetUtcNow(), [new MachineCheck("reachable", true, "ok")])).Machine!;

        idle.State.Should().Be(MachineState.Idle);
        down.State.Should().Be(MachineState.Down);
    }

    [Fact]
    public void A_machine_that_was_down_keeps_the_reason_in_the_note_of_its_new_qualification_when_the_control_plane_restarts()
    {
        var pool = Pool();
        pool.Register("leipzig", MachineRole.Staging);
        pool.Report("leipzig", Failing(_time.GetUtcNow()));

        var restarted = new MachinePool(_store, _time).Register("leipzig", MachineRole.Staging);

        restarted.State.Should().Be(MachineState.Qualifying);
        restarted.Reason.Should().Contain("it was down").And.Contain("5180");
        restarted.LastQualification.Should().NotBeNull();
    }

    [Fact]
    public void A_qualification_with_no_check_does_not_pass()
    {
        var pool = Pool();
        pool.Register("leipzig", MachineRole.Staging);

        var down = pool.Report("leipzig", new Qualification(_time.GetUtcNow(), [])).Machine!;

        down.State.Should().Be(MachineState.Down);
        down.Reason.Should().Contain("no check");
    }

    [Fact]
    public void The_result_of_a_qualification_is_taken_only_from_a_machine_that_is_being_qualified()
    {
        var pool = Qualified();

        pool.Report("leipzig", Failing(_time.GetUtcNow())).Ok.Should().BeFalse("an Idle machine does not take a result: nothing it says can make it Down by itself");
        pool.Get("leipzig")!.State.Should().Be(MachineState.Idle);
    }

    [Fact]
    public void A_lease_without_a_sign_of_life_ends_into_finished_and_a_heartbeat_keeps_it()
    {
        var pool = Pool(new MachineOptions(TimeSpan.FromMinutes(10)));
        pool.Register("leipzig", MachineRole.Staging);
        pool.Report("leipzig", Passing(_time.GetUtcNow()));
        pool.TryLease("leipzig", "m1");

        _time.Advance(TimeSpan.FromMinutes(9));
        pool.Heartbeat("leipzig", "m1").Should().BeTrue();
        _time.Advance(TimeSpan.FromMinutes(9));
        pool.ExpireLeases().Should().BeEmpty("the heartbeat renewed the lease");

        _time.Advance(TimeSpan.FromMinutes(11));
        var expired = pool.ExpireLeases();

        expired.Should().ContainSingle().Which.State.Should().Be(MachineState.Finished);
        pool.Get("leipzig")!.Reason.Should().Contain("lease expired");
        pool.Heartbeat("leipzig", "m1").Should().BeFalse();
    }

    [Fact]
    public void A_long_job_is_not_a_reason_to_lose_the_machine_as_long_as_the_runner_lives()
    {
        var pool = Pool(new MachineOptions(TimeSpan.FromMinutes(30)));
        pool.Register("leipzig", MachineRole.Staging);
        pool.Report("leipzig", Passing(_time.GetUtcNow()));
        pool.TryLease("leipzig", "m1");

        for (var minute = 0; minute < 180; minute += 5)
        {
            _time.Advance(TimeSpan.FromMinutes(5));
            pool.Heartbeat("leipzig", "m1");
            pool.ExpireLeases();
        }

        pool.Get("leipzig")!.State.Should().Be(MachineState.InProcess, "three hours of troubleshooting");
    }

    [Fact]
    public void A_machine_that_was_in_process_when_the_control_plane_stopped_is_recorded_as_finished_then_qualified()
    {
        var pool = Qualified();
        pool.TryLease("leipzig", "m1");

        var restarted = new MachinePool(_store, _time);
        restarted.Register("leipzig", MachineRole.Staging);

        _store.Transitions("leipzig").Select(transition => transition.To).TakeLast(2).Should().Equal(MachineState.Finished, MachineState.Qualifying);
    }

    [Fact]
    public void Every_change_is_announced_and_only_the_transitions_of_the_rules_exist()
    {
        var pool = Qualified();
        _announced.Clear();

        pool.TryLease("leipzig", "m1");
        pool.Release("leipzig", "m1");

        _announced.Select(machine => machine.State).Should().Equal(MachineState.InProcess, MachineState.Finished);
        MachineRules.Allows(MachineState.Finished, MachineState.Idle).Should().BeFalse("a finished machine is qualified first");
        MachineRules.Allows(MachineState.Down, MachineState.InProcess).Should().BeFalse();
        MachineRules.Allows(MachineState.Idle, MachineState.Finished).Should().BeFalse();
        MachineRules.Allows(null, MachineState.Idle).Should().BeFalse("a machine is created while being qualified");
    }

    [Fact]
    public void A_machine_that_is_idle_can_be_put_down_by_a_check_and_a_machine_that_is_finished_too()
    {
        var pool = Qualified();

        pool.MarkDown("leipzig", "the isolation probe found a way out").Machine!.State.Should().Be(MachineState.Down);
        pool.MarkDown("leipzig", "again").Ok.Should().BeFalse("a machine that is down is already down");
    }
}

public sealed class MachineMetricsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 8, 0, 0, TimeSpan.Zero);

    private static MachineTransition At(int minutes, MachineState to, MachineState? from = null) => new("leipzig", from, to, T0.AddMinutes(minutes), null, null);

    [Fact]
    public void The_time_in_each_state_the_availability_the_utilisation_and_the_lots_are_computed_from_the_transitions()
    {
        // 0-10 qualifying, 10-30 idle, 30-60 in process, 60-70 finished, 70-80 qualifying, 80-100 down, 100-120 qualifying, 120-180 idle
        var transitions = new[]
        {
            At(0, MachineState.Qualifying), At(10, MachineState.Idle), At(30, MachineState.InProcess), At(60, MachineState.Finished), At(70, MachineState.Qualifying),
            At(80, MachineState.Down), At(100, MachineState.Qualifying), At(120, MachineState.Idle),
        };

        var stats = MachineMetrics.Compute(transitions, T0, T0.AddMinutes(180));

        stats.InState[MachineState.InProcess].Should().Be(TimeSpan.FromMinutes(30));
        stats.InState[MachineState.Down].Should().Be(TimeSpan.FromMinutes(20));
        stats.InState[MachineState.Idle].Should().Be(TimeSpan.FromMinutes(80));
        stats.Availability.Should().BeApproximately(1 - (20.0 / 180), 0.0001);
        stats.Utilisation.Should().BeApproximately(30.0 / 180, 0.0001);
        stats.Lots.Should().Be(1);
        stats.MeanLot.Should().Be(TimeSpan.FromMinutes(30));
        stats.DownEvents.Should().Be(1);
        stats.MeanTimeToRepair.Should().Be(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void A_window_that_starts_before_the_machine_was_known_says_nothing_of_that_time_and_a_machine_still_down_has_no_repair_yet()
    {
        var transitions = new[] { At(60, MachineState.Qualifying), At(70, MachineState.Down) };

        var stats = MachineMetrics.Compute(transitions, T0, T0.AddMinutes(130));

        stats.From.Should().Be(T0.AddMinutes(60));
        stats.InState[MachineState.Down].Should().Be(TimeSpan.FromMinutes(60));
        stats.Availability.Should().BeApproximately(1 - (60.0 / 70), 0.0001);
        stats.DownEvents.Should().Be(1);
        stats.MeanTimeToRepair.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_window_is_cut_where_it_ends_and_an_empty_history_is_empty()
    {
        var transitions = new[] { At(0, MachineState.Idle), At(30, MachineState.InProcess), At(90, MachineState.Finished) };

        var stats = MachineMetrics.Compute(transitions, T0.AddMinutes(10), T0.AddMinutes(60));

        stats.InState[MachineState.Idle].Should().Be(TimeSpan.FromMinutes(20));
        stats.InState[MachineState.InProcess].Should().Be(TimeSpan.FromMinutes(30));
        MachineMetrics.Compute([], T0, T0.AddHours(1)).Availability.Should().Be(0);
    }

    [Fact]
    public void A_lot_that_began_before_the_window_counts_in_it_for_the_time_it_has_inside()
    {
        var transitions = new[] { At(0, MachineState.Idle), At(20, MachineState.InProcess), At(80, MachineState.Finished) };

        var stats = MachineMetrics.Compute(transitions, T0.AddMinutes(50), T0.AddMinutes(100));

        stats.Lots.Should().Be(1);
        stats.MeanLot.Should().Be(TimeSpan.FromMinutes(30), "the time of the lot that is inside the window, not the whole lot");
    }
}
