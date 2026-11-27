using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Missions;

namespace Orchestrarr.Host.Missions;

/// <summary>One step of the traveler of an operation (ADR 0056): when, what (the event of the audit trail, which a screen words), in which world when a mission did it, what was said, and how many times in a row the same thing happened.</summary>
public sealed record TravelerStep(DateTimeOffset At, string Event, string? World, string? Text, int Times = 1);

/// <summary>The traveler of an operation: what it is, where it stands, and its steps one after the other, oldest first.</summary>
public sealed record TravelerView(string ProblemId, string Title, string? Kind, string Repository, string State, int Priority, string? Note, IReadOnlyList<TravelerStep> Steps);

/// <summary>
/// The traveler of a lot's operation (ADR 0056, the MES's lot traveler): read from the audit trail (the problem and its missions are the subjects), never a record of its own. The same row ticked again is the same problem: only its
/// latest run is told (what follows the last end before it). The same refusal said again and again while a pipeline runs is told once, with how many times.
/// </summary>
internal static class Traveler
{
    private const int MaxText = 400;

    private static readonly HashSet<string> Ends = new(StringComparer.Ordinal)
    {
        MissionAuditEvents.ProblemClosed,
        MissionAuditEvents.ProblemFailed,
        MissionAuditEvents.ProblemEscalated,
        MissionAuditEvents.QueuedProblemRefused,
    };

    internal static TravelerView Build(FlowProblem shown, IReadOnlyList<Mission> missions, IReadOnlyList<AuditRow> rows)
    {
        var worldOf = missions.ToDictionary(mission => mission.Id, mission => mission.World.ToString(), StringComparer.Ordinal);
        // The latest run: what comes after the last end that is followed by something else
        var start = 0;
        for (var index = 0; index < rows.Count - 1; index++)
        {
            if (Ends.Contains(rows[index].Entry.Event))
            {
                start = index + 1;
            }
        }
        var steps = new List<TravelerStep>();
        foreach (var entry in rows.Skip(start).Select(row => row.Entry))
        {
            var text = Cut(entry.Detail);
            var world = worldOf.GetValueOrDefault(entry.Subject);
            if (steps.Count > 0 && steps[^1] is { } last && last.Event == entry.Event && last.World == world && last.Text == text)
            {
                steps[^1] = last with { Times = last.Times + 1 };
                continue;
            }
            steps.Add(new TravelerStep(entry.At, entry.Event, world, text));
        }
        return new TravelerView(shown.ProblemId, shown.Title, shown.Kind, shown.Repository, shown.State, shown.Priority, shown.Note, steps);
    }

    // What was said, at most MaxText characters, never cutting a character that takes two
    private static string? Cut(string? detail)
    {
        if (detail is not { Length: > MaxText })
        {
            return detail;
        }
        var end = char.IsHighSurrogate(detail[MaxText - 1]) ? MaxText - 1 : MaxText;
        return detail[..end];
    }
}
