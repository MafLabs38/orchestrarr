namespace Orchestrarr.Core.Missions;

/// <summary>
/// The wire format between the control plane and a runner of missions, written once and read by both sides (CLAUDE.md rule 14): a mission that a runner may take (what to do, where, until when), the dossier it hands over,
/// a line of what it does. The mandate itself stays with the control plane in this first version (ADR 0042 point 6).
/// </summary>
public sealed record ClaimedMission(string MissionId, string ProblemId, string ApplicationId, string World, string Objective, string Repository, int? PullRequest, string? HeadSha, DateTimeOffset MandateExpiresAt,
    IReadOnlyList<string> Acts, string? Commit = null, bool Rework = false, string? Remarks = null, int? Replaces = null, string? Instruction = null, string? Machine = null);

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

    /// <summary>Written by a proof in staging of a repository that has nothing to build or run (only pipelines, for example): what was proved is said by its pipeline alone (ADR 0055).</summary>
    public const string NoBuild = "build:none";

    /// <summary>Written by a staging proof that was repaired, with the number of files: what tells the control plane that a repair was made, whatever it still holds of it.</summary>
    public const string Repair = "repair:files=";

    /// <summary>Written by a free agent that pushed its branch: the name and the commit it said it finished at. A claim, never a fact: the control plane reads the branch on the forge.</summary>
    public const string ChangeBranch = "change:branch=";

    /// <summary>
    /// Written by a staging proof that stopped before building because of something that no work of an agent mends in this world: the pull request conflicts with the base branch, or it moved since the maintainer approved it. The
    /// control plane ends the problem at once, without the controller and without a send-back (the next problem on the repository goes on); what a conflict needs is a new pull request from the bot or the maintainer.
    /// </summary>
    public const string Refused = "refused:";

    public const string RefusedConflict = Refused + "conflict";

    public const string RefusedMoved = Refused + "moved";

    /// <summary>The reason written by a refusal marker of a dossier, or null when the dossier holds none.</summary>
    public static string? RefusalOf(IEnumerable<string> evidence) => evidence.FirstOrDefault(entry => entry is RefusedConflict or RefusedMoved);
}

/// <summary>One file of a repair, whole: its path from the root of the repository and its text. The control plane writes it into a commit; it is never applied by anything that reads a diff.</summary>
public sealed record PatchedFile(string Path, string Content);

/// <summary>What the gateway answers when it opened the pull request that carries a repair: the head of the new branch, or why it did not.</summary>
public sealed record MissionPullRequestResponse(bool Opened, int Number, string? Commit, string? Refusal, string? From = null);

/// <summary>What a runner hands over. A value that is missing is a refusal on the control plane's side, whatever the runner sends.</summary>
public sealed record MissionHandOver(string? Claim, string? WhatChangedAndWhy, IReadOnlyList<string>? EvidenceDigests, IReadOnlyList<string>? OpenPoints, bool Complex, string? ComplexReason, string? Commit, IReadOnlyList<PatchedFile>? Files = null, string? Branch = null,
    string? PipelineJustification = null);

/// <summary>
/// What the act gateway answers to a staging runner that asks for the credential of its free agent (ADR 0057): the token, until when it is valid and what it may do, or why none is given. The runner asks again before it expires; a
/// refusal then (the stop, a withdrawn approval, an expired mandate) takes the credential away from the agent.
/// </summary>
/// <c>Retry</c> says that the act was authorised and only the forge did not make the credential now: the one in place is kept and the runner asks again later.
public sealed record PushCredentialResponse(bool Allowed, string? Refusal, string? Token, DateTimeOffset? ExpiresAt, IReadOnlyList<string> Permissions, bool Retry = false);
