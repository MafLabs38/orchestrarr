using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task A_mission_is_started_worked_on_and_handed_over()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.ClaimAsync(Arg.Any<CancellationToken>()).Returns(Mission("staging"));
        plane.StartedAsync("m1", Arg.Any<CancellationToken>()).Returns(true);
        plane.HandOverAsync("m1", Arg.Any<MissionHandOver>(), Arg.Any<CancellationToken>()).Returns(true);
        var shell = Substitute.For<IShell>();
        // No restoring tool: the work is refused inside the proof, and that refusal is handed over like any other dossier
        var worker = Worker(plane, shell, new MissionRunnerOptions { LabTool = null });

        (await worker.TakeOneAsync(Ct)).Should().BeTrue();

        await plane.Received(1).HandOverAsync("m1", Arg.Is<MissionHandOver>(dossier => dossier.Complex), Arg.Any<CancellationToken>());
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
