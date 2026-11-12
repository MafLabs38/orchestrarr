using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Runner;

/// <summary>The shapes that a task must have before anything else looks at it, and the matching of a repository against the patterns of the rights.</summary>
internal static class TaskShape
{

    /// <summary>True when every field of the batch has the shape of a real one. The fields come from the queue, so they are checked like any outside input.</summary>
    public static bool IsWellFormed(TaskBatch batch)
    {
        if (batch.Tasks.Count is 0 or > TaskLimits.MaxTasksPerBatch || batch.Tasks.Count(task => task.Kind == TaskKind.RepairPullRequest) > TaskLimits.MaxRepairsPerBatch || !OrderValidation.IsValidSubject(batch.ApproverSubject) || !OrderValidation.IsValidWorkItem(batch.WorkItemId))
        {
            return false;
        }
        var ordered = batch.Tasks.OrderBy(task => task.Sequence).ToList();
        return ordered.Select((task, index) => task.Sequence == index + 1).All(ok => ok)
            && batch.Tasks.Select(task => task.Id).Distinct().Count() == batch.Tasks.Count
            && batch.Tasks.All(task =>
                Enum.IsDefined(task.Kind)
                && task.PullRequest > 0
                && OrderValidation.IsValidRepository(task.Repository)
                && OrderValidation.IsValidSha(task.HeadSha));
    }

    /// <summary>Whether <paramref name="repository"/> matches a pattern where <c>*</c> stands for any characters. Names are compared without regard to case.</summary>
    public static bool Matches(string pattern, string repository) => RepositoryPattern.Matches(pattern, repository);
}
