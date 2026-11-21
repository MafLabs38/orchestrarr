namespace Orchestrarr.Core.Missions;

/// <summary>
/// The wire format between the control plane and a runner of missions, written once and read by both sides (CLAUDE.md rule 14): a mission that a runner may take (what to do, where, until when), the dossier it hands over,
/// a line of what it does. The mandate itself stays with the control plane in this first version (ADR 0042 point 6).
/// </summary>
public sealed record ClaimedMission(string MissionId, string ProblemId, string ApplicationId, string World, string Objective, string Repository, int? PullRequest, string? HeadSha, DateTimeOffset MandateExpiresAt,
    IReadOnlyList<string> Acts, string? Commit = null, bool Rework = false, string? Remarks = null, int? Replaces = null);

public sealed record MissionClaimResponse(ClaimedMission? Mission);

public sealed record MissionProgressRequest(string? Line);

/// <summary>
/// What the act gateway answers to a runner that asks for a merge or a deployment (ADR 0043): whether the act may be done, and for a merge that was done the commit that it made. A refusal says why, in the words of the checks;
/// <c>Retry</c> says that only a pipeline that is not over yet stands in the way, so that asking again later may change the answer.
/// </summary>
public sealed record MissionActResponse(bool Allowed, string? Refusal, IReadOnlyList<string> Failures, bool Retry, string? Commit);

/// <summary>The state of the pipeline on the commit that a mission works on, read by the control plane from the forge: <c>passing</c>, <c>failing</c>, <c>pending</c> or <c>unknown</c>.</summary>
public sealed record MissionPipelineResponse(string State, string? Commit);

/// <summary>The values of the state of a pipeline on the wire, and the one mapping from what the forge says: one place for the control plane that says them and the runner that waits for them.</summary>
public static class MissionPipelineState
{
    public const string Passing = "passing";
    public const string Failing = "failing";
    public const string Pending = "pending";
    public const string Unknown = "unknown";

    public static string Of(Orchestrarr.Core.Forge.ChecksState state) => state switch
    {
        Orchestrarr.Core.Forge.ChecksState.Passing => Passing,
        Orchestrarr.Core.Forge.ChecksState.Failing => Failing,
        Orchestrarr.Core.Forge.ChecksState.Pending or Orchestrarr.Core.Forge.ChecksState.NotRun => Pending,
        _ => Unknown,
    };
}

/// <summary>The prefixes of the evidence that the runners put in a dossier and that the control plane reads back (one place, read by both sides): the tree that was built and tested in staging, the commit that a merge made, the state of a pipeline.</summary>
public static class MissionEvidence
{
    public const string ArtifactTree = "artifact:tree=";
    public const string MergeCommit = "merge:commit=";
    public const string Pipeline = "pipeline:";

    /// <summary>Written by a staging proof that was repaired, with the number of files: what tells the control plane that a repair was made, whatever it still holds of it.</summary>
    public const string Repair = "repair:files=";

    /// <summary>Written by the Git world when the gateway opened the pull request that carries a repair: <c>repair:pr=N;head=SHA;from=SHA</c>, so that the link survives what the control plane holds in memory.</summary>
    public const string RepairPullRequest = "repair:pr=";
}

/// <summary>One file of a repair, whole: its path from the root of the repository and its text. The control plane writes it into a commit; it is never applied by anything that reads a diff.</summary>
public sealed record PatchedFile(string Path, string Content);

/// <summary>What the gateway answers when it opened the pull request that carries a repair: the head of the new branch, or why it did not.</summary>
public sealed record MissionPullRequestResponse(bool Opened, int Number, string? Commit, string? Refusal, string? From = null);

/// <summary>What a runner hands over. A value that is missing is a refusal on the control plane's side, whatever the runner sends.</summary>
public sealed record MissionHandOver(string? Claim, string? WhatChangedAndWhy, IReadOnlyList<string>? EvidenceDigests, IReadOnlyList<string>? OpenPoints, bool Complex, string? ComplexReason, string? Commit, IReadOnlyList<PatchedFile>? Files = null);
