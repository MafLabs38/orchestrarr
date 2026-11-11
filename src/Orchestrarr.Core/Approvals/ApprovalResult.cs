namespace Orchestrarr.Core.Approvals;

/// <summary>A pull request as the operator saw it on the card: its number and the commit shown. An approval covers exactly these.</summary>
public sealed record SeenPullRequest(int Number, string HeadSha);

/// <summary>One row of the table the operator selected: the pull request at the commit they saw, the steps its row stands for, and a note for the trail (the effective plan).</summary>
public sealed record SelectedProblem(string WorkItemId, int PullRequest, string HeadSha, IReadOnlyList<WorkItems.StepKind> Steps, string? Note);

public sealed record ApprovalResult(IReadOnlyList<Approval> Approvals, ApprovalRefusal? Refusal)
{
    public bool Recorded => Refusal is null;

    public static ApprovalResult Refused(ApprovalRefusal refusal) => new([], refusal);
}
