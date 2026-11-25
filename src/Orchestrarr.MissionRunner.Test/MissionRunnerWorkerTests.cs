using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Missions;
using Orchestrarr.MissionRunner;

namespace Orchestrarr.MissionRunner.Test;

public sealed class MissionRunnerWorkerTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static ClaimedMission Mission(string world) =>
        new("m1", "p1", "app", world, "Prove it", "example/shop", 7, new string('a', 40), DateTimeOffset.UtcNow.AddHours(1), []);

    // The control plane of these tests holds the machine for mission m1, as the real one does once it has leased it
    private static MissionRunnerWorker Worker(IMissionControlPlane plane, IShell shell, MissionRunnerOptions options)
    {
        plane.MachineStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new MachineStateInfo("InProcess", "Staging", "m1"));
        plane.MachineHeartbeatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        return new MissionRunnerWorker(plane, [new StagingProof(shell, options)], options, NullLogger<MissionRunnerWorker>.Instance);
    }

    [Fact]
    public async Task With_no_mission_nothing_is_done()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync("Staging", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<ClaimedMission?>(null));

        (await Worker(plane, Substitute.For<IShell>(), new MissionRunnerOptions()).TakeOneAsync(Ct)).Should().BeFalse();

        await plane.DidNotReceiveWithAnyArgs().StartedAsync(default!, Ct);
    }

    [Fact]
    public async Task A_runner_asks_only_for_the_worlds_it_serves_and_the_later_ones_first()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        var shell = Substitute.For<IShell>();
        var works = new IWorldWork[] { new StagingProof(shell, new MissionRunnerOptions()), new GitWorld(plane, new MissionRunnerOptions()), new ReleaseWorld(plane, shell, new MissionRunnerOptions()) };
        var options = new MissionRunnerOptions { Worlds = ["Staging", "Git"] };

        (await new MissionRunnerWorker(plane, works, options, NullLogger<MissionRunnerWorker>.Instance).TakeOneAsync(Ct)).Should().BeFalse();

        Received.InOrder(() =>
        {
            plane.ClaimAsync("Git", Arg.Any<CancellationToken>());
            plane.ClaimAsync("Staging", Arg.Any<CancellationToken>());
        });
        await plane.DidNotReceive().ClaimAsync("Release", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_mission_that_the_control_plane_does_not_let_start_is_left_alone()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync("Staging", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Mission("Staging"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(false);
        var shell = Substitute.For<IShell>();

        (await Worker(plane, shell, new MissionRunnerOptions()).TakeOneAsync(Ct)).Should().BeFalse();

        await shell.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default, Ct);
    }

    [Fact]
    public async Task A_refused_proof_is_handed_over_like_any_dossier()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync("Staging", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Mission("staging"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(true);
        plane.HandOverAsync("m1", Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>()).Returns(true);
        var shell = Substitute.For<IShell>();
        // No restoring tool: the work is refused inside the proof, and that refusal is handed over like any other dossier
        var worker = Worker(plane, shell, new MissionRunnerOptions { StagingMachine = "staging-1", LabTool = null });

        (await worker.TakeOneAsync(Ct)).Should().BeTrue();

        await plane.Received(1).HandOverAsync("m1", Arg.Is<MissionHandOver>(dossier => dossier.Complex), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_dossier_that_the_control_plane_refuses_does_not_stop_the_runner()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync("Staging", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Mission("Staging"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(true);
        plane.HandOverAsync("m1", Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>()).Returns(false);

        (await Worker(plane, Substitute.For<IShell>(), new MissionRunnerOptions { StagingMachine = "staging-1" }).TakeOneAsync(Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task The_global_stop_cancels_the_work_that_is_going_on_and_nothing_is_handed_over()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync("Staging", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Mission("Staging"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(true);
        plane.StopEngagedAsync(Arg.Any<CancellationToken>()).Returns(true);
        var shell = Substitute.For<IShell>();
        shell.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            // The restoring tool answers at once; the work on the machine goes on until it is cancelled
            if (call.ArgAt<string>(0) != "ssh")
            {
                return new ShellResult(0, string.Empty, TimedOut: false);
            }
            await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(3));
            return new ShellResult(0, string.Empty, TimedOut: false);
        });
        var options = new MissionRunnerOptions { StagingMachine = "staging-1", LabTool = "/lab/lab.sh", StopPollSeconds = 1, Repositories = ["example/*"] };

        (await Worker(plane, shell, options).TakeOneAsync(Ct)).Should().BeTrue();

        await plane.DidNotReceiveWithAnyArgs().HandOverAsync(default!, default!, Ct);
        await plane.Received().ProgressAsync("m1", Arg.Is<string>(line => line.Contains("global stop", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stop_that_cannot_be_read_once_does_not_end_the_watch_and_the_next_read_stops_the_work()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync("Staging", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Mission("Staging"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(true);
        // A read that times out (the HTTP client's own timeout is a cancellation), then the stop is engaged
        plane.StopEngagedAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<bool>(new TaskCanceledException("timed out")), _ => Task.FromResult(true));
        var shell = Substitute.For<IShell>();
        shell.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (call.ArgAt<string>(0) != "ssh")
            {
                return new ShellResult(0, string.Empty, TimedOut: false);
            }
            await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(3));
            return new ShellResult(0, string.Empty, TimedOut: false);
        });
        var options = new MissionRunnerOptions { StagingMachine = "staging-1", LabTool = "/lab/lab.sh", StopPollSeconds = 1, Repositories = ["example/*"] };

        (await Worker(plane, shell, options).TakeOneAsync(Ct)).Should().BeTrue();

        await plane.DidNotReceiveWithAnyArgs().HandOverAsync(default!, default!, Ct);
        await plane.Received(2).StopEngagedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stop_that_stays_unreadable_stops_the_work_in_the_end()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync("Staging", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Mission("Staging"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(true);
        plane.StopEngagedAsync(Arg.Any<CancellationToken>()).Returns<Task<bool>>(_ => throw new HttpRequestException("down"));
        var shell = Substitute.For<IShell>();
        shell.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (call.ArgAt<string>(0) != "ssh")
            {
                return new ShellResult(0, string.Empty, TimedOut: false);
            }
            await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(3));
            return new ShellResult(0, string.Empty, TimedOut: false);
        });
        var options = new MissionRunnerOptions { StagingMachine = "staging-1", LabTool = "/lab/lab.sh", StopPollSeconds = 1, StopReadFailuresAllowed = 2, Repositories = ["example/*"] };

        (await Worker(plane, shell, options).TakeOneAsync(Ct)).Should().BeTrue();

        await plane.DidNotReceiveWithAnyArgs().HandOverAsync(default!, default!, Ct);
        await plane.Received().ProgressAsync("m1", Arg.Is<string>(line => line.Contains("global stop", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_call_that_times_out_during_the_work_is_not_taken_for_a_stop()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync("Staging", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Mission("Staging"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(true);
        plane.ProgressAsync("m1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromException(new TaskCanceledException("the line timed out")));

        var shell = Substitute.For<IShell>();
        shell.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(new ShellResult(0, string.Empty, TimedOut: false));

        var work = () => Worker(plane, shell, new MissionRunnerOptions { StagingMachine = "staging-1", LabTool = "/lab/lab.sh", Repositories = ["example/*"] }).TakeOneAsync(Ct);

        await work.Should().ThrowAsync<TaskCanceledException>("it is the loop of the runner that says why, not the stop");
        await plane.DidNotReceive().ProgressAsync("m1", Arg.Is<string>(line => line.Contains("global stop", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_hand_over_that_fails_on_the_network_is_tried_again_and_a_refusal_is_final()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync("Staging", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Mission("Staging"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(true);
        plane.HandOverAsync("m1", Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<bool>(new HttpRequestException("reset")), _ => Task.FromResult(true));

        (await Worker(plane, Substitute.For<IShell>(), new MissionRunnerOptions { StagingMachine = "staging-1", PollSeconds = 1 }).TakeOneAsync(Ct)).Should().BeTrue();

        await plane.Received(2).HandOverAsync("m1", Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void The_options_name_what_is_missing_and_refuse_a_machine_that_is_not_an_alias()
    {
        var ready = () => new MissionRunnerOptions { ControlPlaneUrl = new Uri("http://127.0.0.1:5180"), Token = "t", StagingMachine = "staging-1", LabTool = "/lab/lab.sh" }.Validate();
        ready.Should().NotThrow();
        var noUrl = () => new MissionRunnerOptions { Token = "t", StagingMachine = "staging-1", LabTool = "/lab/lab.sh" }.Validate();
        noUrl.Should().Throw<InvalidOperationException>().WithMessage("*ControlPlaneUrl*");
        var noMachine = () => new MissionRunnerOptions { ControlPlaneUrl = new Uri("http://127.0.0.1:5180"), Token = "t", LabTool = "/lab/lab.sh" }.Validate();
        noMachine.Should().Throw<InvalidOperationException>().WithMessage("*StagingMachine*");
        var noTool = () => new MissionRunnerOptions { ControlPlaneUrl = new Uri("http://127.0.0.1:5180"), Token = "t", StagingMachine = "staging-1" }.Validate();
        noTool.Should().Throw<InvalidOperationException>().WithMessage("*LabTool*");
        var injected = () => new MissionRunnerOptions { ControlPlaneUrl = new Uri("http://127.0.0.1:5180"), Token = "t", StagingMachine = "a; rm -rf ~", LabTool = "/lab/lab.sh" }.Validate();
        injected.Should().Throw<InvalidOperationException>().WithMessage("*alias*");
    }

    [Theory]
    [InlineData("https://control.example", true)]
    [InlineData("http://127.0.0.1:5180", true)]
    [InlineData("http://192.168.1.10:5180", true)]
    [InlineData("http://control.example", false)]
    [InlineData("http://8.8.8.8", false)]
    public void The_token_goes_over_plain_http_only_inside_the_local_network(string url, bool accepted)
    {
        var options = new MissionRunnerOptions { ControlPlaneUrl = new Uri(url), Token = "t", StagingMachine = "staging-1" };

        var make = () => new MissionControlPlane(new HttpClient(), options);

        if (accepted)
        {
            make.Should().NotThrow();
        }
        else
        {
            make.Should().Throw<InvalidOperationException>().WithMessage("*https*");
        }
    }

    [Fact]
    public void The_token_comes_from_the_setting_or_from_its_file_and_is_required()
    {
        new MissionRunnerOptions { Token = "t" }.TokenValue().Should().Be("t");
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "from-file\n");
            new MissionRunnerOptions { TokenFile = file }.TokenValue().Should().Be("from-file");
        }
        finally
        {
            File.Delete(file);
        }
        var none = () => new MissionRunnerOptions().TokenValue();
        none.Should().Throw<InvalidOperationException>();
    }

    private static MissionRunnerOptions Ready(Action<MissionRunnerOptions> change)
    {
        var options = new MissionRunnerOptions { ControlPlaneUrl = new Uri("http://127.0.0.1:5180"), Token = "t", StagingMachine = "staging-1", LabTool = "/lab/lab.sh", Worlds = ["Staging", "Git", "Release"], DeployMachine = "prod-1" };
        change(options);
        options.Validate();
        return options;
    }

    // A world's work that holds every mission until it is let go, so that the number at work at once can be read with no wait on the clock
    private sealed class Probe(string world, int expected = int.MaxValue, int total = int.MaxValue) : IWorldWork
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inside;

        public string World { get; } = world;

        public int AtOnce { get; private set; }

        public List<string?> Machines { get; } = [];

        public Task Reached => _reached.Task;

        public Task AllTaken => _all.Task;

        public void Let() => _go.TrySetResult();

        public async Task<MissionHandOver> RunAsync(ClaimedMission mission, Func<string, Task> report, CancellationToken cancellationToken)
        {
            int inside;
            lock (Machines)
            {
                Machines.Add(mission.Machine);
                inside = ++_inside;
                AtOnce = Math.Max(AtOnce, inside);
            }
            if (Machines.Count >= total)
            {
                _all.TrySetResult();
            }
            if (inside >= expected)
            {
                _reached.TrySetResult();
            }
            try
            {
                await _go.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (Machines)
                {
                    _inside--;
                }
            }
            return new MissionHandOver("claim", "what changed", [], [], false, null, null);
        }
    }

    [Fact]
    public void A_runner_has_a_lane_for_each_of_its_machines_and_for_each_slot_of_the_git_world_and_says_so()
    {
        var options = Ready(set => (set.StagingMachines, set.DeployMachines, set.GitLanes) = (["staging-1", "staging-2"], ["prod-1"], 3));
        IWorldWork[] works = [new Probe("Staging"), new Probe("Git"), new Probe("Release")];

        var lanes = new MissionRunnerWorker(Substitute.For<IMissionControlPlane>(), works, options, NullLogger<MissionRunnerWorker>.Instance).LanesOf();

        lanes.Select(lane => $"{lane.World}:{lane.Machine}").Should().Equal("Release:prod-1", "Git:", "Git:", "Git:", "Staging:staging-1", "Staging:staging-2");
        options.LanesByWorld().Should().Equal(new Dictionary<string, int> { ["Staging"] = 2, ["Git"] = 3, ["Release"] = 1 });
    }

    [Fact]
    public async Task Two_staging_machines_work_on_two_missions_at_once_each_on_its_own_machine()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        var offered = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        plane.ClaimAsync("Staging", Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var machine = call.ArgAt<string?>(1)!;
            var id = machine == "staging-1" ? "a" : "b";
            return Task.FromResult<ClaimedMission?>(offered.TryAdd(machine, true) ? Mission("Staging") with { MissionId = id } : null);
        });
        plane.StartedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        plane.HandOverAsync(Arg.Any<string>(), Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>()).Returns(true);
        plane.MachineStateAsync("staging-1", Arg.Any<CancellationToken>()).Returns(new MachineStateInfo("InProcess", "Staging", "a"));
        plane.MachineStateAsync("staging-2", Arg.Any<CancellationToken>()).Returns(new MachineStateInfo("InProcess", "Staging", "b"));
        plane.MachineHeartbeatAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var probe = new Probe("Staging", expected: 2);
        var options = Ready(set => (set.Worlds, set.StagingMachines, set.PollSeconds) = (["Staging"], ["staging-1", "staging-2"], 1));
        var worker = new MissionRunnerWorker(plane, [probe], options, NullLogger<MissionRunnerWorker>.Instance);

        await worker.StartAsync(Ct);
        await probe.Reached.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        probe.AtOnce.Should().Be(2, "each machine is a lane of its own");
        probe.Machines.Should().BeEquivalentTo("staging-1", "staging-2");
        probe.Let();
        await worker.StopAsync(CancellationToken.None);

        await plane.Received(1).HandOverAsync("a", Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>());
        await plane.Received(1).HandOverAsync("b", Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_slots_of_the_git_world_work_at_once_and_no_more_than_the_runner_was_given()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        var queue = new System.Collections.Concurrent.ConcurrentQueue<ClaimedMission>(new[] { "a", "b", "c", "d" }.Select(id => Mission("Git") with { MissionId = id }));
        plane.ClaimAsync("Git", Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<ClaimedMission?>(queue.TryDequeue(out var next) ? next : null));
        plane.StartedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        plane.HandOverAsync(Arg.Any<string>(), Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>()).Returns(true);
        var probe = new Probe("Git", expected: 3, total: 4);
        var options = Ready(set => (set.Worlds, set.GitLanes, set.PollSeconds) = (["Git"], 3, 1));
        var worker = new MissionRunnerWorker(plane, [probe], options, NullLogger<MissionRunnerWorker>.Instance);

        await worker.StartAsync(Ct);
        await probe.Reached.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        probe.AtOnce.Should().Be(3);
        queue.Should().HaveCount(1, "the three lanes are at work: the fourth mission is not taken");
        probe.Let();
        await probe.AllTaken.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await worker.StopAsync(CancellationToken.None);

        probe.AtOnce.Should().Be(3);
        probe.Machines.Should().AllSatisfy(machine => machine.Should().BeNull());
    }

    [Fact]
    public async Task An_idle_lane_looks_after_its_own_machine_only_and_a_machine_that_fails_does_not_stop_it()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.MachineStateAsync("staging-1", Arg.Any<CancellationToken>()).Returns(new MachineStateInfo("Idle", "Staging", null));
        plane.MachineStateAsync("staging-2", Arg.Any<CancellationToken>()).Returns(new MachineStateInfo("Finished", "Staging", null));
        var options = Ready(set => (set.Worlds, set.StagingMachines) = (["Staging"], ["staging-1", "staging-2"]));
        var worker = new MissionRunnerWorker(plane, [new Probe("Staging")], options, NullLogger<MissionRunnerWorker>.Instance, new MachineQualifier(Substitute.For<IShell>(), options));

        await worker.MaintainSafelyAsync("staging-2", Ct);
        await worker.MaintainSafelyAsync("staging-1", Ct);

        await plane.Received(1).MachineQualifyingAsync("staging-2", Arg.Any<CancellationToken>());
        await plane.DidNotReceive().MachineQualifyingAsync("staging-1", Arg.Any<CancellationToken>());
        plane.MachineStateAsync("staging-2", Arg.Any<CancellationToken>()).Returns<MachineStateInfo?>(_ => throw new HttpRequestException("down"));
        var again = async () => await worker.MaintainSafelyAsync("staging-2", Ct);
        await again.Should().NotThrowAsync("a machine that fails is told and does not stop its lane");
    }

    [Fact]
    public void The_lanes_of_a_runner_reach_the_control_plane_in_its_hello_as_it_wrote_them()
    {
        var options = Ready(set => (set.StagingMachines, set.GitLanes) = (["staging-1", "staging-2"], 3));
        var hello = new Orchestrarr.Core.Tasks.RunnerHello("runner", "1", [], [], [], false, false, options.LanesByWorld());

        var wire = System.Text.Json.JsonSerializer.Serialize(hello, Orchestrarr.Core.Tasks.RunnerWire.Json);
        var read = System.Text.Json.JsonSerializer.Deserialize<Orchestrarr.Core.Tasks.RunnerHello>(wire, Orchestrarr.Core.Tasks.RunnerWire.Json);

        read!.Lanes.Should().Equal(new Dictionary<string, int> { ["Staging"] = 2, ["Git"] = 3, ["Release"] = 1 });
    }

    [Fact]
    public void The_settings_of_the_lanes_are_checked_before_the_runner_starts()
    {
        Ready(set => set.StagingMachines = ["staging-1", "staging-2"]);

        var twice = () => Ready(set => set.StagingMachines = ["staging-1", "prod-1"]);
        twice.Should().Throw<InvalidOperationException>().WithMessage("*'prod-1' is listed twice*");
        var unreadable = () => Ready(set => set.StagingMachines = ["staging-1", "not an alias"]);
        unreadable.Should().Throw<InvalidOperationException>().WithMessage("*not an alias*");
        var sameWithAnotherCase = () => Ready(set => (set.StagingMachines, set.DeployMachine) = (["Server-1"], "server-1"));
        sameWithAnotherCase.Should().Throw<InvalidOperationException>().WithMessage("*listed twice*", "ssh reads an alias whatever its case");
        var twoReleases = () => Ready(set => set.DeployMachines = ["prod-1", "prod-2"]);
        twoReleases.Should().Throw<InvalidOperationException>().WithMessage("*one deployment machine*");
        var noRelease = () => Ready(set => set.DeployMachine = null);
        noRelease.Should().Throw<InvalidOperationException>().WithMessage("*DeployMachine*");
        Ready(set => (set.StagingMachines, set.StagingMachine) = (["staging-2"], "staging-1")).StagingMachineList().Should().Equal("staging-2");
        foreach (var wrong in new[] { 0, MissionRunnerOptions.MaxGitLanes + 1 })
        {
            var lanes = () => Ready(set => set.GitLanes = wrong);
            lanes.Should().Throw<InvalidOperationException>().WithMessage("*GitLanes*");
        }
    }
}
