namespace Orchestrarr.Core.Missions;

/// <summary>What bounds a mission: the repositories it may touch, the paths it may never touch (pipelines, secrets) and the place it works in (a staging slot, an environment, a branch prefix).</summary>
public sealed record MissionFrame(IReadOnlyList<string> Repositories, IReadOnlyList<string> ForbiddenPaths, string Place);

/// <summary>Time, attempts and turns, never money: the model is the maintainer's subscription (ADR 0036).</summary>
public sealed record MissionBudget(int MaxMinutes, int MaxAttempts, int MaxTurns)
{
    public bool IsValid => MaxMinutes > 0 && MaxAttempts > 0 && MaxTurns > 0;
}

/// <summary>Where a mission is (FUNCTIONAL 5.3). A mission that ended never starts again: a new mandate makes a new mission.</summary>
public enum MissionState
{
    Proposed,
    Mandated,
    Running,
    HandingOver,
    Accepted,
    Ended,
}

/// <summary>How a mission ended. A mission that ends always leaves a dossier and the system in a known state.</summary>
public enum MissionEnd
{
    Done,

    /// <summary>Done, and the worker agent marked it complex: the maintainer is asked to try the application.</summary>
    DoneComplex,

    /// <summary>The mission could not be finished in its budget: nothing half-merged, nothing half-deployed.</summary>
    Failed,

    /// <summary>The kill switch, a revoked mandate or the budget stopped it.</summary>
    Stopped,

    /// <summary>The controller ended it.</summary>
    Refused,
}

/// <summary>Work in one world. The objective says what must be true at the end, never how to get there (FR-MIS-2).</summary>
public sealed record Mission(
    string Id,
    string ProblemId,
    string ApplicationId,
    World World,
    string Objective,
    MissionFrame Frame,
    MissionBudget Budget,
    MissionState State,
    string? MandateId = null,
    int SendBacks = 0,
    MissionEnd? End = null,
    string? EndReason = null)
{
    public bool IsOver => State is MissionState.Accepted or MissionState.Ended;
}

/// <summary>What happens to a mission.</summary>
public enum MissionEvent
{
    MandateIssued,
    Started,
    HandedOver,
    Granted,
    SentBack,
    EndedByController,
    BudgetExhausted,
    Stopped,
    MandateExpired,
}

/// <summary>The result of an event: the new mission, or the reason why the event is not allowed in this state.</summary>
public sealed record MissionTransition(Mission? Mission, string? Refusal)
{
    public bool Allowed => Mission is not null;
}
