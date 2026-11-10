using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor;

/// <summary>The shapes that a task must have before anything else looks at it, and the matching of a repository against the patterns of the rights.</summary>
internal static class TaskShape
{

    /// <summary>True when every field of the batch has the shape of a real one. The fields come from the queue, so they are checked like any outside input.</summary>
    public static bool IsWellFormed(TaskBatch batch)
    {
        if (batch.Tasks.Count is 0 or > TaskLimits.MaxTasksPerBatch || !OrderValidation.IsValidSubject(batch.ApproverSubject) || !OrderValidation.IsValidWorkItem(batch.WorkItemId))
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
    public static bool Matches(string pattern, string repository)
    {
        var parts = pattern.Split('*');
        var position = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i == 0)
            {
                if (!repository.StartsWith(part, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                position = part.Length;
                continue;
            }
            if (i == parts.Length - 1)
            {
                return repository.Length - position >= part.Length && repository.EndsWith(part, StringComparison.OrdinalIgnoreCase);
            }
            var found = repository.IndexOf(part, position, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }
            position = found + part.Length;
        }
        return repository.Length == position;
    }
}
