using System.Text.RegularExpressions;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor;

/// <summary>The shapes that a task must have before anything else looks at it, and the matching of a repository against the patterns of the rights.</summary>
internal static partial class TaskShape
{
    public const int MaxTasks = 50;

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex RepositoryShape();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex CommitShape();

    [GeneratedRegex("^[A-Za-z0-9@._:/+-]{1,200}$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex SubjectShape();

    /// <summary>True when every field of the batch has the shape of a real one. The fields come from the queue, so they are checked like any outside input.</summary>
    public static bool IsWellFormed(TaskBatch batch)
    {
        if (batch.Tasks.Count is 0 or > MaxTasks || !SubjectShape().IsMatch(batch.ApproverSubject) || batch.WorkItemId.Length is 0 or > 300)
        {
            return false;
        }
        var ordered = batch.Tasks.OrderBy(task => task.Sequence).ToList();
        return ordered.Select((task, index) => task.Sequence == index + 1).All(ok => ok)
            && batch.Tasks.Select(task => task.Id).Distinct().Count() == batch.Tasks.Count
            && batch.Tasks.All(task =>
                Enum.IsDefined(task.Kind)
                && task.PullRequest > 0
                && RepositoryShape().IsMatch(task.Repository)
                && CommitShape().IsMatch(task.HeadSha));
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
