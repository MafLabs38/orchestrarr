namespace Orchestrarr.Core.Missions;

/// <summary>
/// The life of a mission as a pure function: the same mission and event always give the same answer, and an event that does not fit the state is refused, never
/// ignored. The control plane stores the result; nothing here touches a store, a clock or a runner.
/// </summary>
public static class MissionMachine
{
    /// <param name="mission">The mission as it is.</param>
    /// <param name="missionEvent">What happens to it.</param>
    /// <param name="loop">The bound on send-backs.</param>
    /// <param name="mandateId">The mandate, for <see cref="MissionEvent.MandateIssued"/>.</param>
    /// <param name="complex">Whether the worker marked the case complex, for <see cref="MissionEvent.Granted"/>.</param>
    /// <param name="reason">Why, for the events that end a mission.</param>
    public static MissionTransition Apply(Mission mission, MissionEvent missionEvent, LoopPolicy loop, string? mandateId = null, bool complex = false, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(mission);
        ArgumentNullException.ThrowIfNull(loop);
        if (mission.IsOver)
        {
            return Refuse(mission, missionEvent, "the mission is over");
        }
        return missionEvent switch
        {
            MissionEvent.MandateIssued => From(mission, MissionState.Proposed, missionEvent, mission with { State = MissionState.Mandated, MandateId = mandateId }),
            MissionEvent.Started => From(mission, MissionState.Mandated, missionEvent, mission with { State = MissionState.Running }),
            MissionEvent.HandedOver => From(mission, MissionState.Running, missionEvent, mission with { State = MissionState.HandingOver }),
            MissionEvent.Granted => From(mission, MissionState.HandingOver, missionEvent, mission with { State = MissionState.Accepted, End = complex ? MissionEnd.DoneComplex : MissionEnd.Done }),
            MissionEvent.SentBack => SendBack(mission, loop),
            MissionEvent.EndedByController => From(mission, MissionState.HandingOver, missionEvent, Ended(mission, MissionEnd.Refused, reason)),
            MissionEvent.BudgetExhausted => Ongoing(mission, missionEvent, Ended(mission, MissionEnd.Failed, reason ?? "the budget is used up")),
            MissionEvent.Stopped => Ongoing(mission, missionEvent, Ended(mission, MissionEnd.Stopped, reason ?? "stopped")),
            MissionEvent.MandateExpired => Ongoing(mission, missionEvent, Ended(mission, MissionEnd.Stopped, reason ?? "the mandate expired")),
            _ => Refuse(mission, missionEvent, "unknown event"),
        };
    }

    private static MissionTransition SendBack(Mission mission, LoopPolicy loop)
    {
        if (mission.State != MissionState.HandingOver)
        {
            return Refuse(mission, MissionEvent.SentBack, "only a mission that handed over is sent back");
        }
        if (mission.SendBacks + 1 > loop.MaxSendBacksPerMission)
        {
            return new MissionTransition(Ended(mission, MissionEnd.Failed, "the controller sent it back too many times"), null);
        }
        return new MissionTransition(mission with { State = MissionState.Running, SendBacks = mission.SendBacks + 1 }, null);
    }

    private static Mission Ended(Mission mission, MissionEnd end, string? reason) => mission with { State = MissionState.Ended, End = end, EndReason = reason };

    // An event that is only valid from one state
    private static MissionTransition From(Mission mission, MissionState required, MissionEvent missionEvent, Mission next) =>
        mission.State == required ? new MissionTransition(next, null) : Refuse(mission, missionEvent, $"it needs a mission that is {required}, this one is {mission.State}");

    // The events that end a mission that has started (or has a mandate): a mission not yet mandated has nothing to stop
    private static MissionTransition Ongoing(Mission mission, MissionEvent missionEvent, Mission next) =>
        mission.State is MissionState.Mandated or MissionState.Running or MissionState.HandingOver
            ? new MissionTransition(next, null)
            : Refuse(mission, missionEvent, "the mission has no mandate to end");

    private static MissionTransition Refuse(Mission mission, MissionEvent missionEvent, string why) => new(null, $"{missionEvent} is refused: {why} ({mission.Id})");
}
