namespace Orchestrarr.Core.Missions;

/// <summary>What a mission hands over (FR-MIS-1): the claim, what changed and why, the evidence by digest, and the complexity mark with its reason. The controller reads it as **untrusted data**.</summary>
public sealed record Dossier(
    string MissionId,
    string Claim,
    string WhatChangedAndWhy,
    IReadOnlyList<string> EvidenceDigests,
    IReadOnlyList<string> OpenPoints,
    bool Complex,
    string? ComplexReason);

public enum ControllerDecisionKind
{
    Grant,
    SendBack,
    End,
    Escalate,
}

/// <summary>The controller's verdict (FR-CTL-4): grant the next mandate, send the mission back with remarks, end it, or escalate to the maintainer, always with reasons.</summary>
public sealed record ControllerDecision(ControllerDecisionKind Kind, IReadOnlyList<string> Reasons, string? Remarks)
{
    /// <summary>A decision without reasons is not accepted; a send-back without remarks is of no use to the worker.</summary>
    public bool IsWellFormed => Reasons.Count > 0 && (Kind != ControllerDecisionKind.SendBack || !string.IsNullOrWhiteSpace(Remarks));
}

/// <summary>
/// What the controller is allowed to do with the world a plan allows (FR-CTL-5): it cannot grant a world the plan does not reach, and it never waives a check of the code.
/// </summary>
public static class ControllerLimits
{
    public static bool MayGrant(ControllerDecision decision, World nextWorld, World? highestAllowed, IReadOnlyList<PreconditionFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(failures);
        return decision.IsWellFormed
            && decision.Kind == ControllerDecisionKind.Grant
            && failures.Count == 0
            && highestAllowed is { } highest
            && nextWorld <= highest;
    }
}
