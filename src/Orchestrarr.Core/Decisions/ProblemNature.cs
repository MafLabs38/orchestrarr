using Orchestrarr.Core.Forge;

namespace Orchestrarr.Core.Decisions;

/// <summary>
/// What a row really is, in the words of a person (ADR 0054): the kind of a row says how it was found (an update, an alert, an image), the nature says **what it concerns**. A bot's pull request that raises a tool of the pipeline
/// (<c>actions/checkout</c>) is a dependency update by kind and a tool of the pipeline by nature: it never changes the application that is deployed.
/// </summary>
public enum ProblemNature
{
    /// <summary>Nothing more to say than the kind.</summary>
    Other,

    /// <summary>A library of the application has a newer version.</summary>
    LibraryUpdate,

    /// <summary>Several libraries are raised together in one pull request.</summary>
    LibrariesUpdate,

    /// <summary>A tool of the build pipeline (a GitHub Action), not of the application.</summary>
    PipelineToolUpdate,

    /// <summary>The base image of the application's container (the <c>FROM</c> line of its Dockerfile).</summary>
    BaseImageUpdate,

    /// <summary>A known flaw in a library that is used, with no pull request to mend it.</summary>
    SecurityAlert,

    /// <summary>A container image that is older than the current one, with no pull request.</summary>
    ImageBehind,

    /// <summary>An image run under a tag that moves.</summary>
    FloatingImage,

    /// <summary>The pipeline of the repository fails on its default branch.</summary>
    FailingPipeline,
}

/// <summary>Where the nature of a row is read: the branch and the labels of a bot's pull request, and the kind of any other row.</summary>
public static class ProblemNatures
{
    /// <summary>
    /// A bot says what it raises in the name of its branch (<c>dependabot/github_actions/actions/checkout-7</c>, <c>dependabot/docker/node-26-alpine</c>, <c>dependabot/npm_and_yarn/multi-…</c>) and in its labels. A pull request
    /// that says nothing of the sort (a person's, a bot of another kind) is taken for a library: that is what a dependency bump is by default.
    /// </summary>
    public static ProblemNature OfPullRequest(PullRequestInfo pullRequest)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        var parts = pullRequest.HeadBranch.Split('/');
        var ecosystem = parts.Length > 2 && parts[0] is "dependabot" or "renovate" ? parts[1] : string.Empty;
        if (ecosystem is "github_actions" || pullRequest.Labels.Contains("github_actions", StringComparer.OrdinalIgnoreCase))
        {
            return ProblemNature.PipelineToolUpdate;
        }
        if (ecosystem is "docker" || pullRequest.Labels.Contains("docker", StringComparer.OrdinalIgnoreCase))
        {
            return ProblemNature.BaseImageUpdate;
        }
        return parts.Length > 2 && parts[^1].StartsWith("multi-", StringComparison.Ordinal) ? ProblemNature.LibrariesUpdate : ProblemNature.LibraryUpdate;
    }

    /// <summary>The nature of a row that is not a bot's pull request: what its kind says, and for an image, whether it is written in a <c>FROM</c> of a Dockerfile (the base image, whose runtime the application runs on).</summary>
    public static ProblemNature OfRow(ProblemRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Kind == ProblemKind.ImageUpdate && row.Locations is { } places && places.Any(place => place.Form == Infrastructure.VersionForm.DockerfileFrom)
            ? ProblemNature.BaseImageUpdate
            : OfKind(row.Kind);
    }

    /// <summary>The nature of a row that is not a bot's pull request, from its kind alone.</summary>
    public static ProblemNature OfKind(ProblemKind kind) => kind switch
    {
        ProblemKind.DependencyAlert => ProblemNature.SecurityAlert,
        ProblemKind.ImageUpdate => ProblemNature.ImageBehind,
        ProblemKind.FloatingImage => ProblemNature.FloatingImage,
        ProblemKind.FailingCi => ProblemNature.FailingPipeline,
        _ => ProblemNature.Other,
    };
}
