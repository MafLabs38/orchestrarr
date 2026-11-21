using Orchestrarr.Core.Missions;
using Orchestrarr.MissionRunner;

namespace Orchestrarr.MissionRunner.Test;

public sealed class GitAndReleaseWorldTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly string Merge = new('b', 40);

    private static MissionRunnerOptions Options() => new()
    {
        StagingMachine = "staging-1",
        LabTool = "/lab/lab.sh",
        DeployMachine = "prod-1",
        DeployHost = "10.0.0.20",
        Repositories = ["example/*"],
        PipelineAskSeconds = 0,
        PipelineWaitMinutes = 1,
    };

    private static ClaimedMission Mission(string world) => new("m1", "p1", "shop", world, "Prove it", "example/shop", 7, new string('a', 40), DateTimeOffset.UtcNow.AddHours(1), [], Merge);

    private static MissionActResponse Merged(string? commit = null) => new(true, null, [], Retry: false, commit ?? Merge);

    private static MissionActResponse Waiting() => new(false, "a check of the code failed", ["CiNotGreenOnExactCommit"], Retry: true, null);

    private static MissionPipelineResponse Pipeline(string state) => new(state, Merge);

    private static Func<string, Task> Quiet => _ => Task.CompletedTask;

    // --- the Git world

    [Fact]
    public async Task The_merge_is_asked_again_while_only_the_pipeline_stands_in_the_way_then_the_pipeline_of_the_integration_branch_is_waited_for()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.MergeAsync("m1", Arg.Any<CancellationToken>()).Returns(Waiting(), Waiting(), Merged());
        plane.PipelineAsync("m1", Arg.Any<CancellationToken>()).Returns(Pipeline("pending"), Pipeline("passing"));
        var lines = new List<string>();

        var dossier = await new GitWorld(plane, Options()).RunAsync(Mission("Git"), line => { lines.Add(line); return Task.CompletedTask; }, Ct);

        dossier.Claim.Should().Contain("merged").And.Contain("green");
        dossier.Commit.Should().Be(Merge);
        dossier.OpenPoints.Should().BeEmpty();
        dossier.EvidenceDigests.Should().Contain($"merge:commit={Merge}").And.Contain("pipeline:passing");
        await plane.Received(3).MergeAsync("m1", Arg.Any<CancellationToken>());
        lines.Should().Contain(line => line.Contains("Waiting for the pipeline of the pull request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_merge_that_the_gateway_refuses_for_good_is_handed_over_as_a_refusal_with_no_commit()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.MergeAsync("m1", Arg.Any<CancellationToken>()).Returns(new MissionActResponse(false, "a check of the code failed", ["DiffTouchesForbiddenPath"], Retry: false, null));

        var dossier = await new GitWorld(plane, Options()).RunAsync(Mission("Git"), Quiet, Ct);

        dossier.Claim.Should().Contain("was not merged").And.Contain("DiffTouchesForbiddenPath");
        dossier.Commit.Should().BeNull();
        await plane.DidNotReceive().PipelineAsync("m1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_pipeline_that_fails_on_the_integration_branch_is_said_with_the_merge_commit()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.MergeAsync("m1", Arg.Any<CancellationToken>()).Returns(Merged());
        plane.PipelineAsync("m1", Arg.Any<CancellationToken>()).Returns(Pipeline("failing"));

        var dossier = await new GitWorld(plane, Options()).RunAsync(Mission("Git"), Quiet, Ct);

        dossier.Claim.Should().Contain("is failing");
        dossier.Commit.Should().Be(Merge);
        dossier.OpenPoints.Should().ContainSingle();
    }

    [Fact]
    public async Task A_pipeline_that_never_ends_is_not_waited_for_without_end()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.MergeAsync("m1", Arg.Any<CancellationToken>()).Returns(Merged());
        plane.PipelineAsync("m1", Arg.Any<CancellationToken>()).Returns(Pipeline("pending"));
        var options = Options();
        options.PipelineWaitMinutes = 0;

        var dossier = await new GitWorld(plane, options).RunAsync(Mission("Git"), Quiet, Ct);

        dossier.Claim.Should().Contain("pending");
        dossier.OpenPoints.Should().ContainSingle();
    }

    // --- the release world

    private sealed class ScriptedShell(Func<string, ShellResult> answer) : IShell
    {
        public List<string> Scripts { get; } = [];

        public Task<ShellResult> RunAsync(string file, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Scripts.Add(string.Join(' ', arguments));
            return Task.FromResult(answer(arguments[^1]));
        }
    }

    private static ShellResult Ok(string output) => new(0, output, TimedOut: false);

    private static IMissionControlPlane Allowing()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.DeployAsync("m1", Arg.Any<CancellationToken>()).Returns(new MissionActResponse(true, null, [], Retry: false, Merge));
        return plane;
    }

    [Fact]
    public async Task A_release_that_the_gateway_allows_is_built_on_the_deployment_machine_verified_and_handed_over_with_its_commit_and_the_previous_version()
    {
        var shell = new ScriptedShell(script => script.Contains("git clone", StringComparison.Ordinal) ? Ok("PREVIOUS orch-shop:11111111") : Ok("GET /health answers 200"));
        var lines = new List<string>();

        var dossier = await new ReleaseWorld(Allowing(), shell, Options()).RunAsync(Mission("Release"), line => { lines.Add(line); return Task.CompletedTask; }, Ct);

        dossier.Claim.Should().Contain("deployed on prod-1").And.Contain("10.0.0.20:").And.Contain("answers 200");
        dossier.Commit.Should().Be(Merge);
        dossier.OpenPoints.Should().BeEmpty();
        dossier.EvidenceDigests.Should().Contain("previous:orch-shop:11111111");
        shell.Scripts.Should().HaveCount(2).And.OnlyContain(script => script.Contains("prod-1", StringComparison.Ordinal));
        shell.Scripts[0].Should().Contain($"git checkout --quiet -f {Merge}").And.Contain("docker build").And.Contain("orch-example-shop");
        shell.Scripts[0].Should().NotContain("docker compose", "a compose file is not deployed here");
        shell.Scripts[0].Should().Contain("is used by", "a port that another application holds stops the deployment");
        lines.Should().Contain(line => line.Contains("Verifying", StringComparison.Ordinal)).And.Contain(line => line.Contains("answers on 10.0.0.20:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_deployment_that_is_not_allowed_touches_no_machine()
    {
        var plane = Substitute.For<IMissionControlPlane>();
        plane.DeployAsync("m1", Arg.Any<CancellationToken>()).Returns(new MissionActResponse(false, "a check of the code failed", ["ArtifactNotProvedInStaging"], Retry: false, null));
        var shell = new ScriptedShell(_ => Ok(string.Empty));

        var dossier = await new ReleaseWorld(plane, shell, Options()).RunAsync(Mission("Release"), Quiet, Ct);

        dossier.Claim.Should().Contain("was not allowed").And.Contain("ArtifactNotProvedInStaging");
        dossier.Commit.Should().BeNull();
        shell.Scripts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_release_that_does_not_verify_says_so_names_the_previous_version_and_rolls_nothing_back()
    {
        var shell = new ScriptedShell(script => script.Contains("git clone", StringComparison.Ordinal) ? Ok("PREVIOUS orch-shop:11111111") : new ShellResult(1, "no healthy answer after a minute; last status: exited", TimedOut: false));

        var dossier = await new ReleaseWorld(Allowing(), shell, Options()).RunAsync(Mission("Release"), Quiet, Ct);

        dossier.Claim.Should().Contain("verification failed");
        dossier.WhatChangedAndWhy.Should().Contain("Nothing was rolled back").And.Contain("11111111");
        dossier.Commit.Should().Be(Merge);
        shell.Scripts.Should().HaveCount(2).And.NotContain(script => script.Contains("docker rm -f orch-shop:11111111", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_deployment_that_does_not_complete_is_not_verified()
    {
        var shell = new ScriptedShell(_ => new ShellResult(3, "nothing to deploy: no Dockerfile and no compose file", TimedOut: false));

        var dossier = await new ReleaseWorld(Allowing(), shell, Options()).RunAsync(Mission("Release"), Quiet, Ct);

        dossier.Claim.Should().Contain("did not complete");
        shell.Scripts.Should().ContainSingle("the verification is for what was deployed");
    }

    [Theory]
    [InlineData("example")]
    [InlineData("example/shop; rm -rf ~")]
    [InlineData("other/shop")]
    public async Task A_repository_that_is_unreadable_or_not_accepted_is_refused_before_anything_is_asked_or_run(string repository)
    {
        var plane = Substitute.For<IMissionControlPlane>();
        var shell = new ScriptedShell(_ => Ok(string.Empty));

        var dossier = await new ReleaseWorld(plane, shell, Options()).RunAsync(Mission("Release") with { Repository = repository }, Quiet, Ct);

        dossier.Claim.Should().StartWith("Nothing was deployed");
        shell.Scripts.Should().BeEmpty();
        await plane.DidNotReceive().DeployAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_repository_always_lands_on_the_same_port_and_the_same_container_name()
    {
        var world = new ReleaseWorld(Substitute.For<IMissionControlPlane>(), new ScriptedShell(_ => Ok(string.Empty)), Options());

        world.PortOf("example/shop").Should().Be(world.PortOf("example/shop")).And.BeInRange(8100, 8199);
        ReleaseWorld.AppName("example/Shop API").Should().Be("example-shop-api");
        ReleaseWorld.AppName("other/shop").Should().NotBe(ReleaseWorld.AppName("example/shop"), "two owners with one repository name do not share a container");
    }

    [Fact]
    public void The_options_ask_for_a_deployment_machine_of_its_own_to_serve_the_release_world()
    {
        var ready = new MissionRunnerOptions { ControlPlaneUrl = new Uri("http://127.0.0.1:5180"), Token = "t", StagingMachine = "staging-1", LabTool = "/lab/lab.sh", Worlds = ["Staging", "Git", "Release"], DeployMachine = "prod-1" };
        ready.Validate();

        var same = () => { ready.DeployMachine = "staging-1"; ready.Validate(); };
        same.Should().Throw<InvalidOperationException>().WithMessage("*DeployMachine*");
        var none = () => { ready.DeployMachine = null; ready.Validate(); };
        none.Should().Throw<InvalidOperationException>().WithMessage("*DeployMachine*");
    }
}
