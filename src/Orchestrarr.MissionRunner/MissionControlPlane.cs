using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Orchestrarr.Core.Missions;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.MissionRunner;

/// <summary>The runner's side of the mission routes of the control plane. It only connects out: it asks, it reports, it never listens.</summary>
public interface IMissionControlPlane
{
    /// <summary>The mission of the given world whose mandate names this runner, if there is one.</summary>
    Task<ClaimedMission?> ClaimAsync(string world, CancellationToken cancellationToken);

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

    public async Task<ClaimedMission?> ClaimAsync(string world, CancellationToken cancellationToken)
    {
        // The hello is the one of the typed-task registry on purpose, as an interim that the table still reads (ADR 0042 point 6(b)); only the kind that this runner does is announced.
        // The runner says what it accepts and the world it serves: a mission of another world is not even offered (it would block those behind it)
        var hello = new RunnerHello(_options.Name, typeof(MissionControlPlane).Assembly.GetName().Version?.ToString() ?? "0", [TaskKind.ValidatePullRequest], _options.Repositories, [], SecondFactorRequired: false);
        using var response = await _http.PostAsJsonAsync($"/api/v1/runner/missions/claim?world={Uri.EscapeDataString(world)}", new ClaimRequest(hello), RunnerWire.Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<MissionClaimResponse>(MissionWire.Json, cancellationToken))?.Mission;
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
