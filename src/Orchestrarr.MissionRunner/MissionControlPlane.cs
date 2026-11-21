using System.Net.Http.Headers;
using System.Net.Http.Json;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.MissionRunner;

/// <summary>The runner's side of the mission routes of the control plane. It only connects out: it asks, it reports, it never listens.</summary>
public interface IMissionControlPlane
{
    Task<ClaimedMission?> ClaimAsync(CancellationToken cancellationToken);

    /// <summary>Says that the work starts: false when the control plane refuses (the mandate expired, the mission is not the runner's any more).</summary>
    Task<bool> StartedAsync(string missionId, CancellationToken cancellationToken);

    Task ProgressAsync(string missionId, string line, CancellationToken cancellationToken);

    Task<bool> HandOverAsync(string missionId, MissionHandOver dossier, CancellationToken cancellationToken);
}

public sealed class MissionControlPlane : IMissionControlPlane
{
    private readonly HttpClient _http;
    private readonly MissionRunnerOptions options;

    public MissionControlPlane(HttpClient http, MissionRunnerOptions options)
    {
        _http = http;
        this.options = options;
        var url = options.ControlPlaneUrl ?? throw new InvalidOperationException("MissionRunner:ControlPlaneUrl is required.");
        _http.BaseAddress = url;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.TokenValue());
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<ClaimedMission?> ClaimAsync(CancellationToken cancellationToken)
    {
        var hello = new RunnerHello(options.Name, typeof(MissionControlPlane).Assembly.GetName().Version?.ToString() ?? "0", [TaskKind.ValidatePullRequest, TaskKind.MergePullRequest], options.Repositories, [], SecondFactorRequired: false);
        using var response = await _http.PostAsJsonAsync("/api/v1/runner/missions/claim", new ClaimRequest(hello), RunnerWire.Json, cancellationToken);
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
        using var response = await _http.PostAsJsonAsync($"/api/v1/runner/missions/{Uri.EscapeDataString(missionId)}/progress", new { line }, MissionWire.Json, cancellationToken);
    }

    public async Task<bool> HandOverAsync(string missionId, MissionHandOver dossier, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync($"/api/v1/runner/missions/{Uri.EscapeDataString(missionId)}/handover", dossier, MissionWire.Json, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
