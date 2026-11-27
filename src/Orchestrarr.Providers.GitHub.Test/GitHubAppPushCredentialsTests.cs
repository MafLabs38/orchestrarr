using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace Orchestrarr.Providers.GitHub.Test;

/// <summary>A push credential is an installation token of the agents' App, for one repository, with the permission on the workflows only when it is asked for, and handed out only when the forge granted exactly that (ADR 0057).</summary>
public sealed class GitHubAppPushCredentialsTests : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly RSA _key = RSA.Create(2048);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _key.Dispose();

    private sealed class Forge(string? granted, bool guarded = true, bool mainExists = true, bool mainGuarded = true, bool failRules = false) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Authorization, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (failRules && path.Contains("/rules/", StringComparison.Ordinal))
            {
                Requests.Add((request.Method, path, request.Headers.Authorization?.Parameter, string.Empty));
                throw new HttpRequestException("the forge did not answer");
            }
            Requests.Add((request.Method, path, request.Headers.Authorization?.Parameter, request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            var body = path switch
            {
                "/repos/example/shop/installation" => """{ "id": 4242 }""",
                "/app/installations/4242/access_tokens" when granted is not null => $$"""{ "token": "ghs_the_token", "expires_at": "2026-11-27T10:00:00Z", "permissions": {{granted}} }""",
                "/repos/example/shop" => """{ "default_branch": "develop" }""",
                "/repos/example/shop/rules/branches/develop" => guarded ? """[ { "type": "pull_request" }, { "type": "update" }, { "type": "deletion" } ]""" : """[ { "type": "pull_request" }, { "type": "deletion" } ]""",
                "/repos/example/shop/branches/main" when mainExists => """{ "name": "main" }""",
                "/repos/example/shop/rules/branches/main" => mainGuarded ? """[ { "type": "update" }, { "type": "deletion" } ]""" : """[ { "type": "update" } ]""",
                "/installation/token" => "{}",
                _ => null,
            };
            return body is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private GitHubAppPushCredentials Credentials(Forge forge) => GitHubAppPushCredentials.Create(new GitHubOptions(), "Iv23example", _key.ExportRSAPrivateKeyPem(), forge, _time);

    [Fact]
    public async Task A_credential_is_asked_for_one_repository_with_the_permissions_of_the_operation_under_an_assertion_signed_by_the_app()
    {
        var forge = new Forge("""{ "contents": "write", "metadata": "read" }""");
        using var credentials = Credentials(forge);

        var issued = await credentials.IssueAsync("example/shop", pipelines: false, Ct);

        issued.Should().NotBeNull();
        issued.Token.Should().Be("ghs_the_token");
        issued.ExpiresAt.Should().Be(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
        issued.Permissions.Should().Equal("contents:write");
        var asked = forge.Requests.Single(request => request.Path.EndsWith("/access_tokens", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(asked.Body);
        body.RootElement.GetProperty("repositories").EnumerateArray().Select(name => name.GetString()).Should().Equal("shop");
        body.RootElement.GetProperty("permissions").EnumerateObject().Select(permission => $"{permission.Name}:{permission.Value.GetString()}").Should().Equal("contents:write");

        var parts = asked.Authorization!.Split('.');
        parts.Should().HaveCount(3);
        _key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Should().BeTrue("the assertion is signed with the App's key");
        using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
        payload.RootElement.GetProperty("iss").GetString().Should().Be("Iv23example");
        (payload.RootElement.GetProperty("exp").GetInt64() - _time.GetUtcNow().ToUnixTimeSeconds()).Should().BeLessThanOrEqualTo(600, "the forge refuses an assertion valid more than ten minutes");
    }

    [Fact]
    public async Task The_permission_on_the_workflows_is_asked_only_when_the_operation_justifies_it()
    {
        var forge = new Forge("""{ "contents": "write", "workflows": "write", "metadata": "read" }""");
        using var credentials = Credentials(forge);

        var issued = await credentials.IssueAsync("example/shop", pipelines: true, Ct);

        issued!.Permissions.Should().Equal("contents:write", "workflows:write");
        forge.Requests.Single(request => request.Path.EndsWith("/access_tokens", StringComparison.Ordinal)).Body.Should().Contain("\"workflows\":\"write\"");
    }

    [Theory]
    [InlineData("""{ "contents": "write", "workflows": "write", "metadata": "read" }""")]
    [InlineData("""{ "contents": "write", "administration": "write" }""")]
    [InlineData("""{ "contents": "read" }""")]
    public async Task A_token_that_may_do_more_or_less_than_what_was_asked_is_not_handed_out(string granted)
    {
        using var credentials = Credentials(new Forge(granted));

        (await credentials.IssueAsync("example/shop", pipelines: false, Ct)).Should().BeNull();
    }

    [Theory]
    [InlineData("example/elsewhere")]
    [InlineData("not a repository")]
    public async Task No_credential_is_made_for_a_repository_the_app_is_not_on_or_a_name_that_is_not_one(string repository)
    {
        var forge = new Forge("""{ "contents": "write" }""");
        using var credentials = Credentials(forge);

        (await credentials.IssueAsync(repository, pipelines: false, Ct)).Should().BeNull();
        forge.Requests.Should().NotContain(request => request.Path.EndsWith("/access_tokens", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_credential_is_revoked_with_itself_and_one_that_is_no_longer_valid_counts_as_revoked()
    {
        var forge = new Forge("""{ "contents": "write" }""");
        using var credentials = Credentials(forge);

        (await credentials.RevokeAsync("ghs_the_token", Ct)).Should().BeTrue();
        var request = forge.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Delete);
        request.Path.Should().Be("/installation/token");
        request.Authorization.Should().Be("ghs_the_token", "the token revokes itself; the App's key is not needed");
    }

    [Fact]
    public async Task No_credential_is_handed_out_where_the_forge_does_not_keep_it_from_merging_and_the_one_made_is_revoked()
    {
        var forge = new Forge("""{ "contents": "write" }""", guarded: false);
        using var credentials = Credentials(forge);

        (await credentials.IssueAsync("example/shop", pipelines: false, Ct)).Should().BeNull("a token that writes contents could merge a pull request through the API on a default branch that any writer may update");
        using (var unguardedMain = Credentials(new Forge("""{ "contents": "write" }""", mainGuarded: false)))
        {
            (await unguardedMain.IssueAsync("example/shop", pipelines: false, Ct)).Should().BeNull("a release pull request into main could be merged as well");
        }
        using (var noMain = Credentials(new Forge("""{ "contents": "write" }""", mainExists: false, mainGuarded: false)))
        {
            (await noMain.IssueAsync("example/shop", pipelines: false, Ct)).Should().NotBeNull("a repository with no main has only its default branch to guard");
        }
        forge.Requests.Should().Contain(request => request.Method == HttpMethod.Delete && request.Path == "/installation/token" && request.Authorization == "ghs_the_token");
    }

    [Fact]
    public async Task A_token_made_and_then_not_checked_because_the_forge_failed_is_revoked()
    {
        var forge = new Forge("""{ "contents": "write" }""", failRules: true);
        using var credentials = Credentials(forge);

        (await credentials.IssueAsync("example/shop", pipelines: false, Ct)).Should().BeNull();
        forge.Requests.Should().Contain(request => request.Method == HttpMethod.Delete && request.Path == "/installation/token", "a token that is live is never left behind by an error");
    }

    [Fact]
    public void A_key_that_is_not_a_pem_rsa_key_is_refused_at_start() =>
        FluentActions.Invoking(() => GitHubAppPushCredentials.Create(new GitHubOptions(), "Iv23example", "not a key")).Should().Throw<InvalidOperationException>();

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}
