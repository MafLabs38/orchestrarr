namespace Orchestrarr.Core.Missions;

/// <summary>The answer of the act gateway: the act may be performed now, **on the repository, the commit and the base named here and no other** (the gateway passes the commit to the forge, whose merge call takes the expected head), or why not.</summary>
public sealed record ActAuthorisation(bool Allowed, string? Refusal, IReadOnlyList<PreconditionFailure> Failures, string? Commit = null, string? Repository = null, string? BaseCommit = null)
{
    public static ActAuthorisation Refused(string refusal, IReadOnlyList<PreconditionFailure>? failures = null) => new(false, refusal, failures ?? []);
}
