namespace Orchestrarr.Core.Missions;

/// <summary>The answer of the act gateway: the act may be performed now, **on the commit named in <c>Commit</c> and no other** (the gateway passes it to the forge, whose merge call takes the expected head), or why not.</summary>
public sealed record ActAuthorisation(bool Allowed, string? Refusal, IReadOnlyList<PreconditionFailure> Failures, string? Commit = null)
{
    public static ActAuthorisation Refused(string refusal, IReadOnlyList<PreconditionFailure>? failures = null) => new(false, refusal, failures ?? []);
}
