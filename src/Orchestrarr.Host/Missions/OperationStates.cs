namespace Orchestrarr.Host.Missions;

/// <summary>
/// What an operation does now, said **once** for every screen (ADR 0056): the Flow, the Queue, the Missions cards and the decisions taken read it from <see cref="FlowProblem.Operation"/>, never derive it again. An escalated
/// operation waits for a person: it is not put aside.
/// </summary>
internal static class OperationStates
{
    internal static string Of(FlowProblem problem) => problem.State switch
    {
        "queued" => problem.Activity?.Waiting?.Code switch { "bot" => "waitingBot", "held" => "held", "planning" => "planning", _ => "waitingTurn" },
        "inProgress" => InProgress(problem.Activity),
        "closed" => "done",
        "escalated" => "escalated",
        _ => "putAside",
    };

    /// <summary>The world an operation in progress is in: the one whose mission runs, is read or waits; else the first that has not started (its mandate is about to be issued).</summary>
    internal static FlowStep? CurrentStep(IReadOnlyList<FlowStep> steps) =>
        steps.FirstOrDefault(step => step.State is "running" or "reading" or "waiting") ?? steps.FirstOrDefault(step => step.State == "notStarted") ?? steps.LastOrDefault();

    private static string InProgress(FlowActivity? activity) => activity switch
    {
        { Kind: "running" } => "working",
        { Kind: "controller" } => "judging",
        { Waiting.Code: "machine" } => "waitingMachine",
        { Waiting.Code: "busy" } => "waitingLane",
        { Waiting.Code: "turn" } => "waitingTurn",
        { Waiting.Code: "bot" } => "waitingBot",
        { Waiting.Code: "runner" } => "waitingRunner",
        _ => "waitingPickup",
    };
}
