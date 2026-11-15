namespace Orchestrarr.Core.Missions;

/// <summary>The answer of the act gateway: the act may be performed now, or why not.</summary>
public sealed record ActAuthorisation(bool Allowed, string? Refusal, IReadOnlyList<PreconditionFailure> Failures)
{
    public static ActAuthorisation Refused(string refusal, IReadOnlyList<PreconditionFailure>? failures = null) => new(false, refusal, failures ?? []);
}
