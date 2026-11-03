using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Orchestrarr.Host.Test;

/// <summary>
/// A session that the test gives: the claims of the identity token, written in a header. It stands in for the cookie that the sign-in with
/// the identity provider would have opened, which needs a provider to be tested for real.
/// </summary>
internal sealed class TestSessionHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestSession";
    public const string Header = "X-Test-Claims";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var raw))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var claims = raw.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2)).Select(pair => new Claim(pair[0], pair[1]));
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed class SignInApiTests
{
    private const string Shop = "repository:example/shop";
    private const string Operator = "sub=alice-1;preferred_username=alice;groups=orchestrarr-operators";
    private const string NotAnOperator = "sub=bob-2;preferred_username=bob;groups=staff";
    private static readonly string Head = new('c', 40);
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> SignInSettings = new()
    {
        ["Approvals:Mode"] = "OpenIdConnect",
        ["SignIn:Authority"] = "https://sso.example/realms/example",
        ["SignIn:ClientId"] = "orchestrarr",
        ["SignIn:ClientSecret"] = "not-a-real-secret",
        ["SignIn:PublicUrl"] = "https://orchestrarr.example",
        ["Powers:Grants:0:Repository"] = "example/shop",
        ["Powers:Grants:0:Steps:0"] = "validate",
        ["Powers:Grants:0:Steps:1"] = "mergePullRequests",
    };

    private static ForgeApiFactory Factory(Action<Dictionary<string, string?>>? settings = null)
    {
        var all = new Dictionary<string, string?>(SignInSettings);
        settings?.Invoke(all);
        return new ForgeApiFactory { EnvironmentName = "Testing", Settings = all, ServiceOverrides = Session };
    }

    // The provider is not reached: its configuration is given, and the session is read from the header
    private static void Session(IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestSessionHandler>(TestSessionHandler.SchemeName, _ => { });
        services.PostConfigure<AuthenticationOptions>(options => options.DefaultAuthenticateScheme = TestSessionHandler.SchemeName);
        services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            options.Configuration = new OpenIdConnectConfiguration
            {
                Issuer = "https://sso.example/realms/example",
                AuthorizationEndpoint = "https://sso.example/realms/example/auth",
                EndSessionEndpoint = "https://sso.example/realms/example/logout",
            });
    }

    private static HttpClient Client(ForgeApiFactory factory, string? claims = null)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });
        if (claims is not null)
        {
            client.DefaultRequestHeaders.Add(TestSessionHandler.Header, claims);
        }
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static Task<HttpResponseMessage> Approve(HttpClient client) =>
        client.PostAsJsonAsync("/api/v1/approvals", new { workItemId = Shop, steps = new[] { "validate", "mergePullRequests" }, pullRequests = new[] { new { number = 7, headSha = Head } } }, Ct);

    // --- nobody signed in: a status, never a page of the provider, and no data

    [Theory]
    [InlineData("/api/v1/work-items")]
    [InlineData("/api/v1/system/status")]
    [InlineData("/api/v1/approvals")]
    [InlineData("/api/v1/activity")]
    [InlineData("/api/v1/safety")]
    [InlineData("/api/v1/unknown")]
    public async Task Without_a_session_every_route_of_the_api_is_a_401_and_not_a_redirect(string path)
    {
        using var factory = Factory();

        var response = await Client(factory).GetAsync(path, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task Without_a_session_nothing_can_be_approved_or_stopped()
    {
        using var factory = Factory();
        var client = Client(factory);

        (await Approve(client)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- an operator

    [Fact]
    public async Task The_status_says_who_is_signed_in_by_the_name_to_show_and_the_subject_that_counts()
    {
        using var factory = Factory();

        var approvals = (await Json(await Client(factory, Operator).GetAsync("/api/v1/system/status", Ct))).GetProperty("approvals");

        approvals.GetProperty("mode").GetString().Should().Be("openidconnect");
        approvals.GetProperty("enabled").GetBoolean().Should().BeTrue();
        approvals.GetProperty("operator").GetString().Should().Be("alice-1");
        approvals.GetProperty("operatorName").GetString().Should().Be("alice");
    }

    [Fact]
    public async Task An_operator_approves_and_the_trail_names_the_subject_of_the_provider()
    {
        using var factory = Factory();
        var client = Client(factory, Operator);

        var response = await Approve(client);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Json(response)).GetProperty("approvals").EnumerateArray().Single().GetProperty("approver").GetString().Should().Be("alice-1");
        var activity = (await Json(await client.GetAsync("/api/v1/activity", Ct))).EnumerateArray().Single();
        activity.GetProperty("subject").GetString().Should().Be("alice-1");
    }

    [Fact]
    public async Task An_operator_can_engage_and_release_the_global_stop()
    {
        using var factory = Factory();
        var client = Client(factory, Operator);

        (await Json(await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct))).GetProperty("killSwitchEngaged").GetBoolean().Should().BeTrue();
        (await Approve(client)).StatusCode.Should().Be(HttpStatusCode.Locked);
        (await Json(await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = false }, Ct))).GetProperty("killSwitchEngaged").GetBoolean().Should().BeFalse();
    }

    // --- somebody signed in who is not an operator: they can look, they cannot act

    [Fact]
    public async Task Somebody_who_is_not_an_operator_can_read_but_cannot_approve_or_stop()
    {
        using var factory = Factory();
        var client = Client(factory, NotAnOperator);

        (await client.GetAsync("/api/v1/work-items", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var approve = await Approve(client);
        approve.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(approve)).GetProperty("error").GetString().Should().Be("notAnOperator");
        (await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(await client.GetAsync("/api/v1/safety", Ct))).GetProperty("killSwitchEngaged").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_session_without_a_usable_subject_is_nobody()
    {
        using var factory = Factory();

        var response = await Approve(Client(factory, "preferred_username=alice;groups=orchestrarr-operators"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- a page of another site cannot act with the session

    [Theory]
    [InlineData("Origin", "https://evil.example")]
    [InlineData("Origin", "http://orchestrarr.example")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    public async Task A_change_that_does_not_come_from_the_application_is_refused_even_with_a_session(string header, string value)
    {
        using var factory = Factory();
        var client = Client(factory, Operator);
        client.DefaultRequestHeaders.Add(header, value);

        var approve = await Approve(client);
        var stop = await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct);

        approve.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(approve)).GetProperty("error").GetString().Should().Be("originNotAllowed");
        stop.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(await Client(factory, Operator).GetAsync("/api/v1/safety", Ct))).GetProperty("killSwitchEngaged").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_change_from_the_origin_of_the_application_goes_through()
    {
        using var factory = Factory();
        var client = Client(factory, Operator);
        client.DefaultRequestHeaders.Add("Origin", "https://orchestrarr.example");

        (await Approve(client)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // --- signing in sends the browser to the provider, with what the configuration says

    [Fact]
    public async Task Signing_in_sends_the_browser_to_the_provider_with_the_code_flow_and_a_proof_key()
    {
        using var factory = Factory();

        var response = await Client(factory).GetAsync("/auth/login?returnUrl=/status", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).Should().Be("https://sso.example/realms/example/auth");
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        query["response_type"].Should().Be("code");
        query["client_id"].Should().Be("orchestrarr");
        query["code_challenge_method"].Should().Be("S256");
        query["code_challenge"].Should().NotBeNullOrEmpty();
        query["scope"].Should().Be("openid profile");
        query["state"].Should().NotBeNullOrEmpty();
        query["nonce"].Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_address_the_provider_sends_the_code_to_comes_from_the_configuration_not_from_the_request()
    {
        using var factory = Factory();
        var client = Client(factory);
        client.DefaultRequestHeaders.Host = "evil.example";
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "evil.example");

        var response = await client.GetAsync("/auth/login", Ct);

        System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query)["redirect_uri"].Should().Be("https://orchestrarr.example/auth/callback");
    }

    [Fact]
    public async Task The_cookies_of_the_sign_in_are_secure_and_not_readable_by_a_script()
    {
        using var factory = Factory();

        var response = await Client(factory).GetAsync("/auth/login", Ct);

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        cookies.Should().NotBeEmpty();
        cookies.Should().OnlyContain(cookie => cookie.Contains("secure", StringComparison.OrdinalIgnoreCase) && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));
    }

    // --- signing out is a change, never a link

    [Fact]
    public async Task Signing_out_is_a_post_and_the_provider_is_told_who_leaves_and_where_to_go_back()
    {
        using var factory = Factory();
        var client = Client(factory, Operator);

        (await client.GetAsync("/auth/logout", Ct)).StatusCode.Should().BeOneOf(HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound);

        var response = await client.PostAsync("/auth/logout", content: null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.GetLeftPart(UriPartial.Path).Should().Be("https://sso.example/realms/example/logout");
        var query = System.Web.HttpUtility.ParseQueryString(response.Headers.Location.Query);
        query["client_id"].Should().Be("orchestrarr");
        query["post_logout_redirect_uri"].Should().Be("https://orchestrarr.example/");
    }

    [Fact]
    public async Task Signing_out_from_another_site_is_refused()
    {
        using var factory = Factory();
        var client = Client(factory, Operator);
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");

        var response = await client.PostAsync("/auth/logout", content: null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // --- the settings are checked when the host starts

    [Theory]
    [InlineData("SignIn:Authority", "http://sso.example/realms/example")]
    [InlineData("SignIn:Authority", null)]
    [InlineData("SignIn:ClientSecret", null)]
    [InlineData("SignIn:PublicUrl", "http://orchestrarr.example")]
    [InlineData("SignIn:SessionHours", "100")]
    public void A_host_with_a_missing_or_unsafe_setting_does_not_start(string key, string? value)
    {
        using var factory = Factory(settings => settings[key] = value);

        var start = () => factory.CreateClient();

        start.Should().Throw<Exception>().Which.ToString().Should().Contain("SignIn:");
    }

    [Fact]
    public void Turning_the_sign_in_on_without_any_setting_does_not_start_the_host()
    {
        using var factory = new ForgeApiFactory { EnvironmentName = "Testing", Settings = new Dictionary<string, string?> { ["Approvals:Mode"] = "OpenIdConnect" } };

        var start = () => factory.CreateClient();

        start.Should().Throw<Exception>().Which.ToString().Should().Contain("SignIn:");
    }

    [Fact]
    public async Task The_other_modes_have_no_sign_in_routes()
    {
        using var factory = new ForgeApiFactory();

        (await factory.CreateClient().GetAsync("/auth/login", Ct)).StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.OK);
        (await factory.CreateClient().GetAsync("/api/v1/system/status", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
