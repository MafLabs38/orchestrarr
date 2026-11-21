namespace Orchestrarr.Core.Missions;

/// <summary>
/// The wire format between the control plane and a runner of missions, written once and read by both sides (CLAUDE.md rule 14): a mission that a runner may take (what to do, where, until when), the dossier it hands over,
/// a line of what it does. The mandate itself stays with the control plane in this first version (ADR 0042 point 6).
/// </summary>
public sealed record ClaimedMission(string MissionId, string ProblemId, string ApplicationId, string World, string Objective, string Repository, int? PullRequest, string? HeadSha, DateTimeOffset MandateExpiresAt,
    IReadOnlyList<string> Acts);

public sealed record MissionClaimResponse(ClaimedMission? Mission);

public sealed record MissionProgressRequest(string? Line);

/// <summary>What a runner hands over. A value that is missing is a refusal on the control plane's side, whatever the runner sends.</summary>
public sealed record MissionHandOver(string? Claim, string? WhatChangedAndWhy, IReadOnlyList<string>? EvidenceDigests, IReadOnlyList<string>? OpenPoints, bool Complex, string? ComplexReason, string? Commit);
