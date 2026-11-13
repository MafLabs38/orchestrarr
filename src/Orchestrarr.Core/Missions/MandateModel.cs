namespace Orchestrarr.Core.Missions;

/// <summary>Who issued a mandate: the first one of a problem comes from the plan; the next ones only from the controller's decision (ADR 0031). A system rule issues the baseline mission of a staging.</summary>
public enum MandateSource
{
    PlanValidation,
    StandingRule,
    Controller,
    SystemRule,
}

/// <summary>
/// The permission to do one mission in one world (FUNCTIONAL 5.5, ADR 0031): where, which acts, which frame, which budget, until when. It is **bound to one runner and one
/// world** and **single-use** (the nonce). It cannot be widened by its holder: a change of scope is a new mandate.
/// </summary>
public sealed record Mandate(
    string Id,
    string MissionId,
    string ProblemId,
    World World,
    string RunnerId,
    IReadOnlyList<GatedAct> Acts,
    MissionFrame Frame,
    MissionBudget Budget,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    MandateSource Source,
    string Nonce)
{
    /// <summary>The bytes that are signed. Every field is in them, in a fixed order, so that no field can move or be replaced without breaking the signature.</summary>
    public byte[] CanonicalBytes()
    {
        var writer = new CanonicalWriter();
        writer.Text(Id);
        writer.Text(MissionId);
        writer.Text(ProblemId);
        writer.Int((int)World);
        writer.Text(RunnerId);
        writer.Int(Acts.Count);
        foreach (var act in Acts.OrderBy(act => (int)act))
        {
            writer.Int((int)act);
        }
        Strings(writer, Frame.Repositories);
        Strings(writer, Frame.ForbiddenPaths);
        writer.Text(Frame.Place);
        writer.Int(Budget.MaxMinutes);
        writer.Int(Budget.MaxAttempts);
        writer.Int(Budget.MaxTurns);
        writer.Seconds(IssuedAt);
        writer.Seconds(ExpiresAt);
        writer.Int((int)Source);
        writer.Text(Nonce);
        return writer.ToArray();
    }

    private static void Strings(CanonicalWriter writer, IReadOnlyList<string> values)
    {
        writer.Int(values.Count);
        foreach (var value in values.Order(StringComparer.Ordinal))
        {
            writer.Text(value);
        }
    }
}

/// <summary>A mandate and the signature of the control plane.</summary>
public sealed record SignedMandate(Mandate Mandate, byte[] Signature);
