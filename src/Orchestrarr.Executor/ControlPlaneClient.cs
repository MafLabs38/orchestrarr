using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor;

/// <summary>The executor's side of the machine routes of the control plane. It only connects out: it asks, it reports, it never listens.</summary>
public interface IControlPlane
{
    Task<ClaimResult> ClaimAsync(ExecutorHello hello, CancellationToken cancellationToken);

    Task<QueueResult> AppendEventsAsync(Guid batchId, IReadOnlyList<RunEvent> events, CancellationToken cancellationToken);

    Task<QueueResult> CompleteAsync(Guid batchId, BatchOutcome outcome, CancellationToken cancellationToken);
}

public sealed class ControlPlaneClient : IControlPlane
{
    private readonly HttpClient _http;

    public ControlPlaneClient(HttpClient http, ControlPlaneOptions options)
    {
        _http = http;
        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var url))
        {
            throw new InvalidOperationException("Executor:ControlPlane:Url must be an absolute address.");
        }
        // The token travels in a header, so plain HTTP is accepted only inside the machine or the local network
        if (url.Scheme != Uri.UriSchemeHttps && !IsPrivate(url))
        {
            throw new InvalidOperationException("Executor:ControlPlane:Url must be https, except on the local network.");
        }
        _http.BaseAddress = url;
        var token = options.Token;
        if (token.Length == 0 && options.TokenFile.Length > 0)
        {
            token = File.ReadAllText(options.TokenFile).Trim();
        }
        if (token.Length == 0)
        {
            throw new InvalidOperationException("Executor:ControlPlane:Token (or TokenFile) is required.");
        }
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<ClaimResult> ClaimAsync(ExecutorHello hello, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("/api/v1/executor/claim", new ClaimRequest(hello), ExecutorWire.Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClaimResult>(ExecutorWire.Json, cancellationToken) ?? new ClaimResult(ClaimStatus.Unavailable);
    }

    public async Task<QueueResult> AppendEventsAsync(Guid batchId, IReadOnlyList<RunEvent> events, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync($"/api/v1/executor/runs/{batchId:D}/events", new AppendEventsRequest(events), ExecutorWire.Json, cancellationToken);
        return Result(response);
    }

    public async Task<QueueResult> CompleteAsync(Guid batchId, BatchOutcome outcome, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync($"/api/v1/executor/runs/{batchId:D}/complete", new CompleteRequest(outcome), ExecutorWire.Json, cancellationToken);
        return Result(response);
    }

    private static QueueResult Result(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.NoContent or HttpStatusCode.OK => QueueResult.Ok,
        HttpStatusCode.NotFound => QueueResult.NotFound,
        HttpStatusCode.Conflict => QueueResult.NotYours,
        HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest => QueueResult.Invalid,
        _ => QueueResult.Unavailable,
    };

    private static bool IsPrivate(Uri url) =>
        url.IsLoopback
        || (System.Net.IPAddress.TryParse(url.Host, out var address) && IsPrivateAddress(address))
        || url.Host is "localhost";

    private static bool IsPrivateAddress(System.Net.IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168));
    }
}
