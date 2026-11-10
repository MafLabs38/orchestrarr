using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestrarr.Core.Tasks;

/// <summary>What an executor tells the control plane each time it asks for work: who it is, and what it is willing to do. For the screen of its state.</summary>
public sealed record ExecutorHello(string Name, string Version, IReadOnlyList<TaskKind> Kinds, IReadOnlyList<string> Repositories, IReadOnlyList<DelegationInfo> Delegations, bool SecondFactorRequired = true);

/// <summary>A standing delegation, as the executor reports it (it keeps the truth: the control plane only shows it).</summary>
public sealed record DelegationInfo(Guid Id, string Subject, TaskKind Kind, string Repository, DateTimeOffset ExpiresAt, int DailyQuota);

public sealed record ClaimRequest(ExecutorHello Hello);

public sealed record AppendEventsRequest(IReadOnlyList<RunEvent> Events);

public sealed record CompleteRequest(BatchOutcome Outcome);

/// <summary>The one way both ends write and read JSON: camel-case names, enums as words.</summary>
public static class ExecutorWire
{
    public static JsonSerializerOptions Json { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
