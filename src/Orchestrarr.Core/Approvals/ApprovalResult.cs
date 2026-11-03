namespace Orchestrarr.Core.Approvals;

/// <summary>A pull request as the operator saw it on the card: its number and the commit shown. An approval covers exactly these.</summary>
public sealed record SeenPullRequest(int Number, string HeadSha);

public sealed record ApprovalResult(IReadOnlyList<Approval> Approvals, ApprovalRefusal? Refusal)
{
    public bool Recorded => Refusal is null;

    public static ApprovalResult Refused(ApprovalRefusal refusal) => new([], refusal);
}
