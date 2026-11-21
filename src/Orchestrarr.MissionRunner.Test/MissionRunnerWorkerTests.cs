using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Missions;
using Orchestrarr.MissionRunner;

namespace Orchestrarr.MissionRunner.Test;

public sealed class MissionRunnerWorkerTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static ClaimedMission Mission(string world) =>
        new("m1", "p1", "app", world, "Prove it", "example/shop", 7, new string('a', 40), DateTimeOffset.UtcNow.AddHours(1), []);

    private static MissionRunnerWorker Worker(IMissionControlPlane plane, IShell shell, MissionRunnerOptions options) =>
        new(plane, new StagingProof(shell, options), options, NullLogger<MissionRunnerWorker>.Instance);

    [Fact]
    public async Task With_no_mission_nothing_is_done()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync(Arg.Any<CancellationToken>()).Returns((ClaimedMission?)null);

        (await Worker(plane, Substitute.For<IShell>(), new MissionRunnerOptions()).TakeOneAsync(Ct)).Should().BeFalse();

        await plane.DidNotReceiveWithAnyArgs().StartedAsync(default!, Ct);
    }

    [Fact]
    public async Task A_mission_of_another_world_is_not_worked_on()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync(Arg.Any<CancellationToken>()).Returns(Mission("Release"));
        var shell = Substitute.For<IShell>();

        (await Worker(plane, shell, new MissionRunnerOptions { World = "Staging" }).TakeOneAsync(Ct)).Should().BeFalse();

        await plane.DidNotReceiveWithAnyArgs().StartedAsync(default!, Ct);
        await shell.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default, Ct);
    }

    [Fact]
    public async Task A_mission_that_the_control_plane_does_not_let_start_is_left_alone()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync(Arg.Any<CancellationToken>()).Returns(Mission("Staging"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(false);
        var shell = Substitute.For<IShell>();

        (await Worker(plane, shell, new MissionRunnerOptions()).TakeOneAsync(Ct)).Should().BeFalse();

        await shell.DidNotReceiveWithAnyArgs().RunAsync(default!, default!, default, Ct);
    }

    [Fact]
    public async Task A_refused_proof_is_handed_over_like_any_dossier()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync(Arg.Any<CancellationToken>()).Returns(Mission("staging"));
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
        plane.ClaimAsync(Arg.Any<CancellationToken>()).Returns(Mission("Staging"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(true);
        plane.HandOverAsync("m1", Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>()).Returns(false);

        (await Worker(plane, Substitute.For<IShell>(), new MissionRunnerOptions { StagingMachine = "staging-1" }).TakeOneAsync(Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task The_global_stop_cancels_the_work_that_is_going_on_and_nothing_is_handed_over()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync(Arg.Any<CancellationToken>()).Returns(Mission("Staging"));
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
        var options = new MissionRunnerOptions { StagingMachine = "staging-1", LabTool = "/lab/lab.sh", StopPollSeconds = 1 };

        (await Worker(plane, shell, options).TakeOneAsync(Ct)).Should().BeTrue();

        await plane.DidNotReceiveWithAnyArgs().HandOverAsync(default!, default!, Ct);
        await plane.Received().ProgressAsync("m1", Arg.Is<string>(line => line.Contains("global stop", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_stop_that_cannot_be_read_does_not_stop_the_work()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync(Arg.Any<CancellationToken>()).Returns(Mission("Staging"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(true);
        plane.StopEngagedAsync(Arg.Any<CancellationToken>()).Returns<Task<bool>>(_ => throw new HttpRequestException("down"));
        plane.HandOverAsync("m1", Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>()).Returns(true);

        (await Worker(plane, Substitute.For<IShell>(), new MissionRunnerOptions { StagingMachine = "staging-1", StopPollSeconds = 1 }).TakeOneAsync(Ct)).Should().BeTrue();

        await plane.Received(1).HandOverAsync("m1", Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void The_options_name_what_is_missing_and_refuse_a_machine_that_is_not_an_alias()
    {
        var ready = () => new MissionRunnerOptions { ControlPlaneUrl = new Uri("http://127.0.0.1:5180"), Token = "t", StagingMachine = "staging-1" }.Validate();
        ready.Should().NotThrow();
        var noUrl = () => new MissionRunnerOptions { Token = "t", StagingMachine = "staging-1" }.Validate();
        noUrl.Should().Throw<InvalidOperationException>().WithMessage("*ControlPlaneUrl*");
        var noMachine = () => new MissionRunnerOptions { ControlPlaneUrl = new Uri("http://127.0.0.1:5180"), Token = "t" }.Validate();
        noMachine.Should().Throw<InvalidOperationException>().WithMessage("*StagingMachine*");
        var injected = () => new MissionRunnerOptions { ControlPlaneUrl = new Uri("http://127.0.0.1:5180"), Token = "t", StagingMachine = "a; rm -rf ~" }.Validate();
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
}
