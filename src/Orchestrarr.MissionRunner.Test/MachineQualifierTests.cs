using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Machines;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.MissionRunner.Test;

/// <summary>The qualification of a lab machine (ADR 0047) and what the runner does around a mission: it takes the machine with the mission, gives it back, restores and checks it, and keeps its machines in order when it is idle.</summary>
public sealed class MachineQualifierTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static MissionRunnerOptions Options(string[]? canaries = null) => new()
    {
        StagingMachine = "staging-1",
        DeployMachine = "release-1",
        LabTool = "/lab/lab.sh",
        IsolationCanaries = canaries ?? ["10.0.0.1:22", "10.0.0.1:5180"],
        RequiredRoutes = ["registry.example:443"],
    };

    // The shell of a machine: the probe answers what the machine reaches, the lab tool says whether the restore worked
    private static IShell Shell(string probe, int resetExit = 0, int probeExit = 0)
    {
        var shell = Substitute.For<IShell>();
        shell.RunAsync("/lab/lab.sh", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(new ShellResult(resetExit, resetExit == 0 ? "back" : "hypervisor unreachable", TimedOut: false));
        shell.RunAsync("ssh", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(new ShellResult(probeExit, probe, TimedOut: false));
        return shell;
    }

    private const string Isolated = "CLOSED C 10.0.0.1:22\nCLOSED C 10.0.0.1:5180\nOPEN R registry.example:443\n";

    [Fact]
    public async Task A_machine_that_is_restored_reaches_what_it_needs_and_nothing_else_is_qualified()
    {
        var shell = Shell(Isolated);

        var checks = await new MachineQualifier(shell, Options()).QualifyAsync("staging-1", restore: true, Ct);

        checks.Should().OnlyContain(check => check.Passed);
        checks.Select(check => check.Name).Should().Equal("restored", "reachable", "isolation", "routes");
        new Qualification(DateTimeOffset.UtcNow, checks).Passed.Should().BeTrue();
        await shell.Received(1).RunAsync("/lab/lab.sh", Arg.Is<IReadOnlyList<string>>(arguments => arguments.SequenceEqual(new[] { "reset", "staging-1" })), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_machine_that_reaches_an_address_it_must_not_reach_is_not_qualified_and_the_check_names_it()
    {
        var shell = Shell("CLOSED C 10.0.0.1:22\nOPEN C 10.0.0.1:5180\nOPEN R registry.example:443\n");

        var checks = await new MachineQualifier(shell, Options()).QualifyAsync("staging-1", restore: true, Ct);

        var isolation = checks.Single(check => check.Name == "isolation");
        isolation.Passed.Should().BeFalse();
        isolation.Detail.Should().Contain("10.0.0.1:5180");
        new Qualification(DateTimeOffset.UtcNow, checks).Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_canary_isolation_cannot_be_verified_and_a_machine_is_not_qualified()
    {
        var shell = Shell("OPEN R registry.example:443\n");

        var checks = await new MachineQualifier(shell, Options(canaries: [])).QualifyAsync("staging-1", restore: true, Ct);

        checks.Single(check => check.Name == "isolation").Passed.Should().BeFalse();
        checks.Single(check => check.Name == "isolation").Detail.Should().Contain("cannot be verified");
    }

    [Fact]
    public async Task A_machine_that_cannot_reach_its_registries_is_not_qualified()
    {
        var shell = Shell("CLOSED C 10.0.0.1:22\nCLOSED C 10.0.0.1:5180\nCLOSED R registry.example:443\n");

        var checks = await new MachineQualifier(shell, Options()).QualifyAsync("staging-1", restore: true, Ct);

        checks.Single(check => check.Name == "routes").Passed.Should().BeFalse();
        checks.Single(check => check.Name == "routes").Detail.Should().Contain("registry.example:443");
    }

    [Fact]
    public async Task A_restore_that_fails_is_the_only_check_and_the_machine_is_not_probed()
    {
        var shell = Shell(Isolated, resetExit: 1);

        var checks = await new MachineQualifier(shell, Options()).QualifyAsync("staging-1", restore: true, Ct);

        checks.Should().ContainSingle().Which.Should().Match<MachineCheck>(check => check.Name == "restored" && !check.Passed && check.Detail.Contains("hypervisor unreachable"));
        await shell.DidNotReceive().RunAsync("ssh", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_machine_that_does_not_answer_over_ssh_or_gives_an_incomplete_probe_is_not_reachable()
    {
        var silent = await new MachineQualifier(Shell(string.Empty, probeExit: 255), Options()).QualifyAsync("staging-1", restore: true, Ct);
        var partial = await new MachineQualifier(Shell("CLOSED C 10.0.0.1:22\n"), Options()).QualifyAsync("staging-1", restore: true, Ct);

        silent.Single(check => check.Name == "reachable").Passed.Should().BeFalse();
        partial.Single(check => check.Name == "reachable").Passed.Should().BeFalse("a probe that did not try every address proves nothing");
        silent.Should().NotContain(check => check.Name == "isolation");
    }

    [Fact]
    public async Task A_connection_that_is_refused_counts_as_a_way_out_because_something_answered_and_a_probe_that_could_not_run_proves_nothing()
    {
        // The script puts a refusal in the same OPEN as a success (the host answered: the path is not blocked); only a timeout or "no route" is CLOSED
        var scripts = await Capture(Isolated);
        var script = scripts[^1];
        script.Should().Contain("refused").And.Contain("[ $rc -eq 124 ]").And.Contain("ERROR");

        var broken = await new MachineQualifier(Shell("ERROR C 10.0.0.1:22\nCLOSED C 10.0.0.1:5180\nOPEN R registry.example:443\n"), Options()).QualifyAsync("staging-1", restore: true, Ct);

        broken.Single(check => check.Name == "reachable").Passed.Should().BeFalse("an error is not a closed door");
        broken.Single(check => check.Name == "reachable").Detail.Should().Contain("proves nothing");
        broken.Should().NotContain(check => check.Name == "isolation");
    }

    private static async Task<IReadOnlyList<string>> Capture(string probe)
    {
        var scripts = new List<string>();
        var shell = Substitute.For<IShell>();
        shell.RunAsync("/lab/lab.sh", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(new ShellResult(0, "back", TimedOut: false));
        shell.RunAsync("ssh", Arg.Do<IReadOnlyList<string>>(arguments => scripts.Add(arguments[^1])), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(new ShellResult(0, probe, TimedOut: false));
        await new MachineQualifier(shell, Options()).QualifyAsync("staging-1", restore: true, Ct);
        return scripts;
    }

    [Fact]
    public async Task A_release_machine_is_checked_and_never_restored_because_it_keeps_what_it_runs()
    {
        var shell = Shell(Isolated);

        var checks = await new MachineQualifier(shell, Options()).QualifyAsync("release-1", restore: false, Ct);

        checks.Select(check => check.Name).Should().Equal("reachable", "isolation", "routes");
        await shell.DidNotReceive().RunAsync("/lab/lab.sh", Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("host:port")]
    [InlineData("a b:22")]
    [InlineData("host:22; rm -rf ~")]
    public void An_address_of_the_probe_that_is_not_host_and_port_in_plain_characters_is_refused_when_the_settings_are_read(string address)
    {
        var options = new MissionRunnerOptions { ControlPlaneUrl = new Uri("http://127.0.0.1:5180"), Token = new string('t', 40), LabTool = "/lab/lab.sh", StagingMachine = "staging-1", IsolationCanaries = [address] };

        var validate = options.Validate;

        validate.Should().Throw<InvalidOperationException>().WithMessage("*host:port*");
    }

    private static MissionRunnerWorker Worker(IMissionControlPlane plane, IShell shell, MissionRunnerOptions options)
    {
        plane.MachineHeartbeatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        return new MissionRunnerWorker(plane, [new StagingProof(shell, options), new ReleaseWorld(plane, shell, options)], options, NullLogger<MissionRunnerWorker>.Instance, new MachineQualifier(shell, options));
    }

    [Fact]
    public async Task A_mission_takes_its_machine_when_it_is_claimed_and_gives_it_back_and_qualifies_it_when_it_is_over()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync("Staging", "staging-1", Arg.Any<CancellationToken>()).Returns(new ClaimedMission("m1", "p1", "app", "Staging", "Prove it", "example/shop", 7, new string('a', 40), DateTimeOffset.UtcNow.AddHours(1), []));
        plane.MachineStateAsync("staging-1", Arg.Any<CancellationToken>()).Returns(new MachineStateInfo("InProcess", "Staging", "m1"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(true);
        plane.HandOverAsync("m1", Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>()).Returns(true);
        var worker = Worker(plane, Shell(Isolated), Options());

        (await worker.TakeOneAsync(Ct)).Should().BeTrue();

        Received.InOrder(() =>
        {
            plane.HandOverAsync("m1", Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>());
            plane.MachineReleaseAsync("staging-1", "m1", Arg.Any<string?>(), Arg.Any<CancellationToken>());
            plane.MachineQualifyingAsync("staging-1", Arg.Any<CancellationToken>());
            plane.MachineQualifiedAsync("staging-1", Arg.Is<IReadOnlyList<MachineCheck>>(checks => checks.All(check => check.Passed)), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task A_mission_that_cannot_be_started_still_gives_its_machine_back()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync("Staging", "staging-1", Arg.Any<CancellationToken>()).Returns(new ClaimedMission("m1", "p1", "app", "Staging", "Prove it", "example/shop", 7, new string('a', 40), DateTimeOffset.UtcNow.AddHours(1), []));
        plane.MachineStateAsync("staging-1", Arg.Any<CancellationToken>()).Returns(new MachineStateInfo("InProcess", "Staging", "m1"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(false);
        var worker = Worker(plane, Shell(Isolated), Options());

        (await worker.TakeOneAsync(Ct)).Should().BeFalse();

        await plane.Received(1).MachineReleaseAsync("staging-1", "m1", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("InProcess", "another-mission")]
    [InlineData("Idle", null)]
    [InlineData("Qualifying", null)]
    public async Task A_runner_does_not_work_on_a_machine_that_the_control_plane_did_not_confirm_as_held_by_the_mission(string? state, string? holder)
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync("Staging", "staging-1", Arg.Any<CancellationToken>()).Returns(new ClaimedMission("m1", "p1", "app", "Staging", "Prove it", "example/shop", 7, new string('a', 40), DateTimeOffset.UtcNow.AddHours(1), []));
        plane.MachineStateAsync("staging-1", Arg.Any<CancellationToken>()).Returns(state is null ? null : new MachineStateInfo(state, "Staging", holder));
        var worker = Worker(plane, Shell(Isolated), Options());

        (await worker.TakeOneAsync(Ct)).Should().BeFalse();

        await plane.DidNotReceive().StartedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await plane.DidNotReceive().MachineReleaseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_idle_runner_qualifies_a_machine_that_is_finished_and_gives_back_a_lease_that_nobody_works_on()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.MachineStateAsync("staging-1", Arg.Any<CancellationToken>()).Returns(new MachineStateInfo("Finished", "Staging", null));
        plane.MachineStateAsync("release-1", Arg.Any<CancellationToken>()).Returns(new MachineStateInfo("InProcess", "Release", "m9"));
        var options = Options();
        options.Worlds = ["Staging", "Release"];
        var worker = Worker(plane, Shell(Isolated), options);

        await worker.MaintainMachinesAsync(Ct);

        await plane.Received(1).MachineQualifiedAsync("staging-1", Arg.Any<IReadOnlyList<MachineCheck>>(), Arg.Any<CancellationToken>());
        await plane.Received(1).MachineReleaseAsync("release-1", "m9", Arg.Is<string?>(reason => reason!.Contains("no longer working")), Arg.Any<CancellationToken>());
        await plane.DidNotReceive().MachineQualifiedAsync("release-1", Arg.Any<IReadOnlyList<MachineCheck>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_idle_runner_leaves_an_idle_or_a_down_machine_alone_and_one_of_a_world_it_does_not_serve()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.MachineStateAsync("staging-1", Arg.Any<CancellationToken>()).Returns(new MachineStateInfo("Down", "Staging", null));
        plane.MachineStateAsync("release-1", Arg.Any<CancellationToken>()).Returns(new MachineStateInfo("Finished", "Release", null));
        var worker = Worker(plane, Shell(Isolated), Options());

        await worker.MaintainMachinesAsync(Ct);

        await plane.DidNotReceive().MachineQualifyingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task One_machine_that_fails_to_be_maintained_does_not_keep_the_other_from_being_maintained()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.MachineStateAsync("staging-1", Arg.Any<CancellationToken>()).Returns<MachineStateInfo?>(_ => throw new HttpRequestException("gone"));
        plane.MachineStateAsync("release-1", Arg.Any<CancellationToken>()).Returns(new MachineStateInfo("Finished", "Release", null));
        var options = Options();
        options.Worlds = ["Staging", "Release"];
        var worker = Worker(plane, Shell(Isolated), options);

        await worker.MaintainMachinesAsync(Ct);

        await plane.Received(1).MachineQualifiedAsync("release-1", Arg.Any<IReadOnlyList<MachineCheck>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_timeout_while_giving_a_machine_back_is_told_and_hides_nothing_of_how_the_mission_ended()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync("Staging", "staging-1", Arg.Any<CancellationToken>()).Returns(new ClaimedMission("m1", "p1", "app", "Staging", "Prove it", "example/shop", 7, new string('a', 40), DateTimeOffset.UtcNow.AddHours(1), []));
        plane.MachineStateAsync("staging-1", Arg.Any<CancellationToken>()).Returns(new MachineStateInfo("InProcess", "Staging", "m1"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(true);
        plane.HandOverAsync("m1", Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>()).Returns(true);
        plane.MachineReleaseAsync("staging-1", "m1", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new TaskCanceledException("timed out")));
        var worker = Worker(plane, Shell(Isolated), Options());

        (await worker.TakeOneAsync(Ct)).Should().BeTrue("the mission was handed over; a machine that could not be given back is taken up again by the maintenance");

        await plane.Received(1).HandOverAsync("m1", Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>());
    }
}
