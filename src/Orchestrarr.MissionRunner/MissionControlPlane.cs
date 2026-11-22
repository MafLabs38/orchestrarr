using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Orchestrarr.Core.Machines;
using Orchestrarr.Core.Missions;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.MissionRunner;

/// <summary>The runner's side of the mission routes of the control plane. It only connects out: it asks, it reports, it never listens.</summary>
public interface IMissionControlPlane
{
    /// <summary>The mission of the given world whose mandate names this runner, if there is one.</summary>
    Task<ClaimedMission?> ClaimAsync(string world, CancellationToken cancellationToken);

    /// <summary>
    /// The same, for a world that works on a lab machine (ADR 0047): the mission is offered only with a lease on that machine, which must be Idle.
    /// </summary>
    Task<ClaimedMission?> ClaimAsync(string world, string? machine, CancellationToken cancellationToken);

    /// <summary>The state of a machine that this runner works on, or null when the control plane does not know it.</summary>
    Task<MachineStateInfo?> MachineStateAsync(string machine, CancellationToken cancellationToken);

    /// <summary>A sign of life while the machine is held: the lease goes on. False when the control plane says that the mission no longer holds the machine (the lease expired or was given back): the work must stop.</summary>
    Task<bool> MachineHeartbeatAsync(string machine, string missionId, CancellationToken cancellationToken);

    /// <summary>The mission is over: the machine is given back (Finished, to be restored and qualified).</summary>
    Task MachineReleaseAsync(string machine, string missionId, string? reason, CancellationToken cancellationToken);

    /// <summary>The restore and the checks of a machine begin.</summary>
    Task MachineQualifyingAsync(string machine, CancellationToken cancellationToken);

    /// <summary>The checks that were made on the machine: Idle if all passed, Down with why if one failed.</summary>
    Task MachineQualifiedAsync(string machine, IReadOnlyList<MachineCheck> checks, CancellationToken cancellationToken);

    /// <summary>Asks the act gateway to merge the pull request of the mission: the control plane checks the facts itself and merges with its own credential.</summary>
    Task<MissionActResponse> MergeAsync(string missionId, CancellationToken cancellationToken);

    /// <summary>Asks the act gateway whether the commit that the controller accepted may be deployed now.</summary>
    Task<MissionActResponse> DeployAsync(string missionId, CancellationToken cancellationToken);

    /// <summary>Asks the act gateway to open the pull request that carries the repair of the staging world, if there is one: it makes the branch and the commit itself. <c>Opened</c> false with no refusal: there was nothing to open.</summary>
    Task<MissionPullRequestResponse> OpenPullRequestAsync(string missionId, CancellationToken cancellationToken);

    /// <summary>The pipeline of the commit that the mission works on, as the control plane reads it from the forge.</summary>
    Task<MissionPipelineResponse> PipelineAsync(string missionId, CancellationToken cancellationToken);

    /// <summary>Says that the work starts: false when the control plane refuses (the mandate expired, the mission is not the runner's any more).</summary>
    Task<bool> StartedAsync(string missionId, CancellationToken cancellationToken);

    Task ProgressAsync(string missionId, string line, CancellationToken cancellationToken);

    Task<bool> HandOverAsync(string missionId, MissionHandOver dossier, CancellationToken cancellationToken);

    /// <summary>Whether the global stop is engaged: read while a mission runs, so that the stop reaches the work that is going on.</summary>
    Task<bool> StopEngagedAsync(CancellationToken cancellationToken);
}

public sealed class MissionControlPlane : IMissionControlPlane
{
    private readonly HttpClient _http;
    private readonly MissionRunnerOptions _options;

    public MissionControlPlane(HttpClient http, MissionRunnerOptions options)
    {
        _http = http;
        _options = options;
        var url = options.ControlPlaneUrl ?? throw new InvalidOperationException("MissionRunner:ControlPlaneUrl is required.");
        // The token travels in a header, so plain HTTP is accepted only inside the machine or the local network (as for the Runner of tasks)
        if (url.Scheme != Uri.UriSchemeHttps && !IsPrivate(url))
        {
            throw new InvalidOperationException("MissionRunner:ControlPlaneUrl must be https, except on the local network.");
        }
        _http.BaseAddress = url;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.TokenValue());
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public Task<ClaimedMission?> ClaimAsync(string world, CancellationToken cancellationToken) => ClaimAsync(world, null, cancellationToken);

    public async Task<ClaimedMission?> ClaimAsync(string world, string? machine, CancellationToken cancellationToken)
    {
        // The hello is the one of the typed-task registry on purpose, as an interim that the table still reads (ADR 0042 point 6(b)); only the kind that this runner does is announced.
        // The runner says what it accepts and the world it serves: a mission of another world is not even offered (it would block those behind it)
        var hello = new RunnerHello(_options.Name, typeof(MissionControlPlane).Assembly.GetName().Version?.ToString() ?? "0", [TaskKind.ValidatePullRequest], _options.Repositories, [], SecondFactorRequired: false);
        using var response = await _http.PostAsJsonAsync($"/api/v1/runner/missions/claim?world={Uri.EscapeDataString(world)}{(machine is null ? string.Empty : $"&machine={Uri.EscapeDataString(machine)}")}", new ClaimRequest(hello), RunnerWire.Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MissionClaimResponse>(MissionWire.Json, cancellationToken))?.Mission;
    }

    public async Task<MachineStateInfo?> MachineStateAsync(string machine, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"/api/v1/runner/machines/{Uri.EscapeDataString(machine)}", cancellationToken);
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<MachineStateInfo>(MissionWire.Json, cancellationToken) : null;
    }

    public async Task<bool> MachineHeartbeatAsync(string machine, string missionId, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync($"/api/v1/runner/machines/{Uri.EscapeDataString(machine)}/heartbeat", new { missionId }, MissionWire.Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MachineOk>(MissionWire.Json, cancellationToken))?.Ok ?? false;
    }

    public async Task MachineReleaseAsync(string machine, string missionId, string? reason, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync($"/api/v1/runner/machines/{Uri.EscapeDataString(machine)}/release", new { missionId, reason }, MissionWire.Json, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task MachineQualifyingAsync(string machine, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync($"/api/v1/runner/machines/{Uri.EscapeDataString(machine)}/qualifying", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task MachineQualifiedAsync(string machine, IReadOnlyList<MachineCheck> checks, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync($"/api/v1/runner/machines/{Uri.EscapeDataString(machine)}/qualification",
            new { checks = checks.Select(check => new { name = check.Name, passed = check.Passed, detail = check.Detail }) }, MissionWire.Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        // The control plane takes the report only from a machine that is being qualified: a report that it refused is said, not lost
        if ((await response.Content.ReadFromJsonAsync<MachineOk>(MissionWire.Json, cancellationToken))?.Ok != true)
        {
            throw new InvalidOperationException($"the control plane did not take the qualification of {machine}");
        }
    }

    public async Task<bool> StartedAsync(string missionId, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync($"/api/v1/runner/missions/{Uri.EscapeDataString(missionId)}/started", content: null, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task ProgressAsync(string missionId, string line, CancellationToken cancellationToken)
    {
        // A line that is refused is lost, not a reason to stop the work
        using var response = await _http.PostAsJsonAsync($"/api/v1/runner/missions/{Uri.EscapeDataString(missionId)}/progress", new MissionProgressRequest(line), MissionWire.Json, cancellationToken);
    }

    public async Task<bool> HandOverAsync(string missionId, MissionHandOver dossier, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync($"/api/v1/runner/missions/{Uri.EscapeDataString(missionId)}/handover", dossier, MissionWire.Json, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public Task<MissionActResponse> MergeAsync(string missionId, CancellationToken cancellationToken) => PostActAsync(missionId, "merge", cancellationToken);

    public Task<MissionActResponse> DeployAsync(string missionId, CancellationToken cancellationToken) => PostActAsync(missionId, "deploy", cancellationToken);

    private async Task<MissionActResponse> PostActAsync(string missionId, string act, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync($"/api/v1/runner/missions/{Uri.EscapeDataString(missionId)}/acts/{act}", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MissionActResponse>(MissionWire.Json, cancellationToken) ?? new MissionActResponse(false, "the control plane gave no answer", [], Retry: false, null);
    }

    public async Task<MissionPullRequestResponse> OpenPullRequestAsync(string missionId, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync($"/api/v1/runner/missions/{Uri.EscapeDataString(missionId)}/acts/open-pull-request", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MissionPullRequestResponse>(MissionWire.Json, cancellationToken) ?? new MissionPullRequestResponse(false, 0, null, "the control plane gave no answer");
    }

    public async Task<MissionPipelineResponse> PipelineAsync(string missionId, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync($"/api/v1/runner/missions/{Uri.EscapeDataString(missionId)}/pipeline", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MissionPipelineResponse>(MissionWire.Json, cancellationToken) ?? new MissionPipelineResponse("unknown", null);
    }

    public async Task<bool> StopEngagedAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("/api/v1/runner/stop", cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StopStatus>(RunnerWire.Json, cancellationToken))?.Engaged ?? true;
    }

    private static bool IsPrivate(Uri url)
    {
        if (url.IsLoopback)
        {
            return true;
        }
        return IPAddress.TryParse(url.Host, out var address) && (address.GetAddressBytes() is [10, ..] or [192, 168, ..] or [172, >= 16 and <= 31, ..]);
    }
}

/// <summary>What the control plane says of a machine of this runner: its state, its role, and the mission that holds it.</summary>
public sealed record MachineStateInfo(string State, string Role, string? MissionId);

internal sealed record MachineOk(bool Ok);
