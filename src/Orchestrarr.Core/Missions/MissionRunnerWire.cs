namespace Orchestrarr.Core.Missions;

/// <summary>
/// The wire format between the control plane and a runner of missions, written once and read by both sides (CLAUDE.md rule 14): a mission that a runner may take (what to do, where, until when), the dossier it hands over,
/// a line of what it does. The mandate itself stays with the control plane in this first version (ADR 0042 point 6).
/// </summary>
public sealed record ClaimedMission(string MissionId, string ProblemId, string ApplicationId, string World, string Objective, string Repository, int? PullRequest, string? HeadSha, DateTimeOffset MandateExpiresAt,
    IReadOnlyList<string> Acts, string? Commit = null, bool Rework = false, string? Remarks = null);

public sealed record MissionClaimResponse(ClaimedMission? Mission);

public sealed record MissionProgressRequest(string? Line);

/// <summary>
/// What the act gateway answers to a runner that asks for a merge or a deployment (ADR 0043): whether the act may be done, and for a merge that was done the commit that it made. A refusal says why, in the words of the checks;
/// <c>Retry</c> says that only a pipeline that is not over yet stands in the way, so that asking again later may change the answer.
/// </summary>
public sealed record MissionActResponse(bool Allowed, string? Refusal, IReadOnlyList<string> Failures, bool Retry, string? Commit);

/// <summary>The state of the pipeline on the commit that a mission works on, read by the control plane from the forge: <c>passing</c>, <c>failing</c>, <c>pending</c> or <c>unknown</c>.</summary>
public sealed record MissionPipelineResponse(string State, string? Commit);

/// <summary>What a runner hands over. A value that is missing is a refusal on the control plane's side, whatever the runner sends.</summary>
public sealed record MissionHandOver(string? Claim, string? WhatChangedAndWhy, IReadOnlyList<string>? EvidenceDigests, IReadOnlyList<string>? OpenPoints, bool Complex, string? ComplexReason, string? Commit);
