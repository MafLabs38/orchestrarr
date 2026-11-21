using System.Text.Json;

namespace Orchestrarr.MissionRunner;

/// <summary>A mission as the control plane hands it to the runner whose mandate names it.</summary>
public sealed record ClaimedMission(string MissionId, string ProblemId, string ApplicationId, string World, string Objective, string Repository, int? PullRequest, string? HeadSha, DateTimeOffset MandateExpiresAt,
    IReadOnlyList<string> Acts);

public sealed record MissionClaimResponse(ClaimedMission? Mission);

public sealed record MissionHandOver(string Claim, string WhatChangedAndWhy, IReadOnlyList<string> EvidenceDigests, IReadOnlyList<string> OpenPoints, bool Complex, string? ComplexReason, string? Commit);

internal static class MissionWire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
