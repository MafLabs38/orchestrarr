using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Forge;
using Orchestrarr.Core.Infrastructure;

namespace Orchestrarr.Core.Test.Decisions;

/// <summary>What a row really is (ADR 0054): read from the branch and the labels of a bot's pull request, and from the kind of any other row.</summary>
public sealed class ProblemNatureTests
{
    private static PullRequestInfo Pull(string branch, params string[] labels) =>
        new(1, "t", new Uri("https://forge.example/o/r/pull/1"), "dependabot[bot]", branch, false, ChecksState.Passing, MergeState.Clean, labels, [], DateTimeOffset.UnixEpoch, "abc");

    [Theory]
    [InlineData("dependabot/github_actions/actions/checkout-7", ProblemNature.PipelineToolUpdate)]
    [InlineData("dependabot/docker/node-26-alpine", ProblemNature.BaseImageUpdate)]
    [InlineData("dependabot/npm_and_yarn/eslint-10.15.0", ProblemNature.LibraryUpdate)]
    [InlineData("dependabot/pip/pytest-9.2.0", ProblemNature.LibraryUpdate)]
    [InlineData("dependabot/npm_and_yarn/multi-0a3c4c2672", ProblemNature.LibrariesUpdate)]
    [InlineData("renovate/actions-checkout-7.x", ProblemNature.LibraryUpdate)]
    [InlineData("lab/case-01-lodash", ProblemNature.LibraryUpdate)]
    public void A_bot_says_what_it_raises_in_its_branch(string branch, ProblemNature nature) =>
        ProblemNatures.OfPullRequest(Pull(branch)).Should().Be(nature);

    [Theory]
    [InlineData("github_actions", ProblemNature.PipelineToolUpdate)]
    [InlineData("docker", ProblemNature.BaseImageUpdate)]
    [InlineData("python", ProblemNature.LibraryUpdate)]
    public void A_label_says_it_when_the_branch_does_not(string label, ProblemNature nature) =>
        ProblemNatures.OfPullRequest(Pull("bot/some-branch", "dependencies", label)).Should().Be(nature);

    [Theory]
    [InlineData(ProblemKind.DependencyAlert, ProblemNature.SecurityAlert)]
    [InlineData(ProblemKind.ImageUpdate, ProblemNature.ImageBehind)]
    [InlineData(ProblemKind.FloatingImage, ProblemNature.FloatingImage)]
    [InlineData(ProblemKind.FailingCi, ProblemNature.FailingPipeline)]
    [InlineData(ProblemKind.ExposedSecret, ProblemNature.Other)]
    [InlineData(ProblemKind.DormantRepository, ProblemNature.Other)]
    public void Any_other_row_is_what_its_kind_says(ProblemKind kind, ProblemNature nature) =>
        ProblemNatures.OfKind(kind).Should().Be(nature);

    [Theory]
    [InlineData(VersionForm.DockerfileFrom, ProblemNature.BaseImageUpdate)]
    [InlineData(VersionForm.ImageLine, ProblemNature.ImageBehind)]
    public void An_image_written_in_the_from_of_a_dockerfile_is_the_base_image(VersionForm form, ProblemNature nature)
    {
        var row = new ProblemRow("r", "w", "shop", ProblemKind.ImageUpdate, "node 20 to 22", null, null, null, null, null, null, null, Ceiling.NoAction, CeilingReason.None, false,
            Locations: [new CodeLocation("o/r", "Dockerfile", 1, form, new ImageReference("node", "20", null))]);

        ProblemNatures.OfRow(row).Should().Be(nature);
    }
}
