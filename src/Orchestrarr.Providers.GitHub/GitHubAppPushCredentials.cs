using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Orchestrarr.Core.Forge;

namespace Orchestrarr.Providers.GitHub;

/// <summary>
/// Push credentials made by a **GitHub App** (ADR 0057): an installation token for **one repository**, valid an hour at most, with <c>contents: write</c> and, only when it is asked for, <c>workflows: write</c>. The App's private key
/// stays with the control plane; it signs a short assertion that the forge exchanges for the token. A token whose permissions are not the ones asked for is not handed out.
/// </summary>
public sealed partial class GitHubAppPushCredentials : IPushCredentials, IDisposable
{
    private readonly HttpClient _http;

    private readonly string _clientId;

    private readonly RSA _key;

    private readonly TimeProvider _time;

    private GitHubAppPushCredentials(HttpClient http, string clientId, RSA key, TimeProvider time)
    {
        _http = http;
        _clientId = clientId;
        _key = key;
        _time = time;
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9._-]{1,100}$", RegexOptions.None, 250)]
    private static partial Regex RepositoryName();

    [GeneratedRegex(@"^[A-Za-z0-9.]{1,64}$", RegexOptions.None, 250)]
    private static partial Regex ClientId();

    /// <summary>The App is named by its client id (or its numeric id) and its private key in PEM; the key is read once, here.</summary>
    public static GitHubAppPushCredentials Create(GitHubOptions options, string clientId, string privateKeyPem, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(clientId) || !ClientId().IsMatch(clientId))
        {
            throw new InvalidOperationException("The client id of the agents' GitHub App must be a plain name.");
        }
        var key = RSA.Create();
        try
        {
            key.ImportFromPem(privateKeyPem);
        }
        catch (ArgumentException failure)
        {
            key.Dispose();
            throw new InvalidOperationException("The private key of the agents' GitHub App cannot be read (a PEM RSA key is expected).", failure);
        }
        var http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = options.ApiUrl, Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds) };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("orchestrarr", "1"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return new GitHubAppPushCredentials(http, clientId, key, time ?? TimeProvider.System);
    }

    public async Task<PushCredential?> IssueAsync(string repositoryFullName, bool pipelines, CancellationToken cancellationToken)
    {
        if (!RepositoryName().IsMatch(repositoryFullName))
        {
            return null;
        }
        try
        {
            var assertion = Assertion();
            using var installation = await SendAsync(HttpMethod.Get, $"repos/{repositoryFullName}/installation", assertion, null, cancellationToken);
            if (installation?.RootElement.TryGetProperty("id", out var id) != true || !id.TryGetInt64(out var installationId))
            {
                return null;
            }
            Dictionary<string, string> asked = pipelines ? new() { ["contents"] = "write", ["workflows"] = "write" } : new() { ["contents"] = "write" };
            var name = repositoryFullName[(repositoryFullName.IndexOf('/', StringComparison.Ordinal) + 1)..];
            using var issued = await SendAsync(HttpMethod.Post, $"app/installations/{installationId.ToString(CultureInfo.InvariantCulture)}/access_tokens", assertion, new { repositories = new[] { name }, permissions = asked }, cancellationToken);
            if (issued is null || !issued.RootElement.TryGetProperty("token", out var token) || token.GetString() is not { Length: > 0 } value
                || !issued.RootElement.TryGetProperty("expires_at", out var expires) || !expires.TryGetDateTimeOffset(out var expiresAt)
                || !issued.RootElement.TryGetProperty("permissions", out var granted) || granted.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            // What the forge granted is read back: a token that may do more than was asked (a workflow write that was not asked for, any permission beyond metadata) is not handed out
            var permissions = granted.EnumerateObject().Where(permission => permission.Name != "metadata").ToDictionary(permission => permission.Name, permission => permission.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
            return permissions.Count == asked.Count && asked.All(pair => permissions.TryGetValue(pair.Key, out var level) && level == pair.Value)
                ? new PushCredential(value, expiresAt, [.. asked.Select(pair => $"{pair.Key}:{pair.Value}")])
                : null;
        }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or InvalidOperationException || failure is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<bool> RevokeAsync(string token, CancellationToken cancellationToken)
    {
        // The token revokes itself: the forge needs nothing else, and an answer that it is no longer valid is as good
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("installation/token", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
        }
        catch (Exception failure) when (failure is HttpRequestException || failure is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _key.Dispose();
    }

    // The assertion of the App (a JSON web token signed with its key, RS256), valid nine minutes from a minute ago: the forge tolerates a clock a little ahead
    private string Assertion()
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["iat"] = now - 60, ["exp"] = now + 540, ["iss"] = _clientId }));
        // RS256 is the only algorithm the forge accepts for an App's assertion, and it is RSASSA-PKCS1-v1_5 with SHA-256 by definition (RFC 7518 §3.3): a signature scheme, not the PKCS#1 v1.5 encryption padding that the rule is about
#pragma warning disable S5542
        var signature = Base64Url(_key.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
#pragma warning restore S5542
        return $"{header}.{payload}.{signature}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, string assertion, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", assertion);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        using var response = await _http.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode ? await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken) : null;
    }
}
