using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Orchestrarr.Host.SignIn;

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

    private static ForgeApiFactory Factory(Action<Dictionary<string, string?>>? settings = null, bool providerReachable = true)
    {
        var all = new Dictionary<string, string?>(SignInSettings);
        settings?.Invoke(all);
        return new ForgeApiFactory { EnvironmentName = "Testing", Settings = all, ServiceOverrides = services => Session(services, providerReachable) };
    }

    // The provider is not reached: its configuration is given, and the session is read from the header
    private static void Session(IServiceCollection services, bool providerReachable)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestSessionHandler>(TestSessionHandler.SchemeName, _ => { });
        services.PostConfigure<AuthenticationOptions>(options => options.DefaultAuthenticateScheme = TestSessionHandler.SchemeName);
        if (!providerReachable)
        {
            // Its address is the one of the configuration, which nobody answers: the discovery document cannot be read
            return;
        }
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
        approvals.GetProperty("isOperator").GetBoolean().Should().BeTrue();
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
        var approvals = (await Json(await client.GetAsync("/api/v1/system/status", Ct))).GetProperty("approvals");
        approvals.GetProperty("isOperator").GetBoolean().Should().BeFalse();
        var approve = await Approve(client);
        approve.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(approve)).GetProperty("error").GetString().Should().Be("notAnOperator");
        (await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(await client.GetAsync("/api/v1/safety", Ct))).GetProperty("killSwitchEngaged").GetBoolean().Should().BeFalse();
        (await Json(await client.GetAsync("/api/v1/approvals", Ct))).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_session_without_a_usable_subject_is_nobody_and_reads_nothing_either()
    {
        using var factory = Factory();
        var client = Client(factory, "preferred_username=alice;groups=orchestrarr-operators");

        (await client.GetAsync("/api/v1/work-items", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Approve(client)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // --- who may read

    [Fact]
    public async Task With_a_reader_group_only_its_members_and_the_operators_read()
    {
        using var factory = Factory(settings => settings["SignIn:ReaderGroup"] = "readers");

        (await Client(factory, "sub=carol-3;groups=readers").GetAsync("/api/v1/work-items", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Client(factory, Operator).GetAsync("/api/v1/work-items", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Client(factory, NotAnOperator).GetAsync("/api/v1/work-items", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client(factory, NotAnOperator).GetAsync("/api/v1/activity", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Approve(Client(factory, "sub=carol-3;groups=readers"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Without_a_reader_group_anybody_who_signed_in_reads()
    {
        using var factory = Factory();

        (await Client(factory, NotAnOperator).GetAsync("/api/v1/work-items", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void Every_route_of_the_api_asks_for_a_session_and_nothing_else_answers_anonymously_but_the_sign_in_and_the_probe()
    {
        using var factory = Factory();
        factory.CreateClient();

        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        endpoints.Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api", StringComparison.Ordinal))
            .Should().NotBeEmpty().And.OnlyContain(endpoint => endpoint.Metadata.GetMetadata<IAuthorizeData>() != null);
        endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<IAuthorizeData>() == null).Select(endpoint => endpoint.RoutePattern.RawText)
            .Should().BeSubsetOf(["/health", "/auth/login", "/auth/logout", "{*path:nonfile}"]);
    }

    [Fact]
    public async Task The_probe_of_the_deployment_answers_without_a_session_and_says_nothing_else()
    {
        using var factory = Factory();

        var response = await Client(factory).GetAsync("/health", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Be("{\"status\":\"ok\"}");
    }

    // --- a page of another site cannot act with the session

    [Theory]
    [InlineData("Origin", "https://evil.example")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    public async Task A_change_that_does_not_come_from_the_application_is_refused_even_with_a_session_and_leaves_no_trace(string header, string value)
    {
        using var factory = Factory();
        var client = Client(factory, Operator);
        client.DefaultRequestHeaders.Add(header, value);

        var approve = await Approve(client);
        var stop = await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct);

        approve.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(approve)).GetProperty("error").GetString().Should().Be("originNotAllowed");
        stop.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var reader = Client(factory, Operator);
        (await Json(await reader.GetAsync("/api/v1/safety", Ct))).GetProperty("killSwitchEngaged").GetBoolean().Should().BeFalse();
        (await Json(await reader.GetAsync("/api/v1/approvals", Ct))).GetArrayLength().Should().Be(0);
        (await Json(await reader.GetAsync("/api/v1/activity", Ct))).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_change_from_the_origin_of_the_application_goes_through()
    {
        using var factory = Factory();
        var client = Client(factory, Operator);
        client.DefaultRequestHeaders.Add("Origin", "https://orchestrarr.example");

        (await Approve(client)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_forged_host_does_not_make_an_origin_the_application_accepts()
    {
        using var factory = Factory();
        var client = Client(factory, Operator);
        client.DefaultRequestHeaders.Host = "evil.example";
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");

        var response = await Approve(client);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(response)).GetProperty("error").GetString().Should().Be("originNotAllowed");
    }

    // --- signing in sends the browser to the provider, with what the configuration says

    private static async Task<HttpResponseMessage> Login(ForgeApiFactory factory, string? returnUrl = null, string? host = null)
    {
        var client = Client(factory);
        if (host is not null)
        {
            client.DefaultRequestHeaders.Host = host;
        }
        return await client.GetAsync(returnUrl is null ? "/auth/login" : $"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}", Ct);
    }

    [Fact]
    public async Task Signing_in_sends_the_browser_to_the_provider_with_the_code_flow_and_a_proof_key()
    {
        using var factory = Factory();

        var response = await Login(factory, "/status");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!;
        location.GetLeftPart(UriPartial.Path).Should().Be("https://sso.example/realms/example/auth");
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        query["response_type"].Should().Be("code");
        query["client_id"].Should().Be("orchestrarr");
        query["code_challenge_method"].Should().Be("S256");
        query["code_challenge"].Should().NotBeNullOrEmpty();
        query["scope"].Should().Be("openid profile");
    }

    [Theory]
    [InlineData("/decisions?severity=high", "/decisions?severity=high")]
    [InlineData("//evil.example", "/")]
    public async Task The_page_the_person_comes_back_to_is_one_of_the_application(string? asked, string expected)
    {
        using var factory = Factory();
        var response = await Login(factory, asked);

        var state = System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query)["state"]!;
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);

        options.StateDataFormat.Unprotect(state)!.RedirectUri.Should().Be(expected);
    }

    [Fact]
    public async Task The_address_the_provider_sends_the_code_to_comes_from_the_configuration_not_from_the_request()
    {
        using var factory = Factory();

        var response = await Login(factory, host: "evil.example");

        System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query)["redirect_uri"].Should().Be("https://orchestrarr.example/auth/callback");
    }

    [Fact]
    public async Task The_cookies_of_the_sign_in_are_secure_and_not_readable_by_a_script()
    {
        using var factory = Factory();

        var response = await Login(factory);

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        cookies.Should().NotBeEmpty();
        cookies.Should().OnlyContain(cookie => cookie.Contains("secure", StringComparison.OrdinalIgnoreCase) && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_session_cookie_is_host_bound_http_only_lax_and_lasts_a_fixed_time_it_does_not_renew()
    {
        using var factory = Factory(settings => settings["SignIn:SessionHours"] = "5");
        factory.CreateClient();

        var cookie = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);

        cookie.Cookie.Name.Should().Be("__Host-orchestrarr.session");
        cookie.Cookie.HttpOnly.Should().BeTrue();
        cookie.Cookie.SameSite.Should().Be(SameSiteMode.Lax);
        cookie.Cookie.SecurePolicy.Should().Be(CookieSecurePolicy.Always);
        cookie.ExpireTimeSpan.Should().Be(TimeSpan.FromHours(5));
        cookie.SlidingExpiration.Should().BeFalse();
    }

    [Fact]
    public void No_token_is_kept_and_the_claims_that_say_a_second_factor_was_passed_stay_in_the_session()
    {
        using var factory = Factory();
        factory.CreateClient();

        var oidc = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);

        oidc.SaveTokens.Should().BeFalse();
        oidc.GetClaimsFromUserInfoEndpoint.Should().BeFalse();
        oidc.UsePkce.Should().BeTrue();
        oidc.MapInboundClaims.Should().BeFalse();
        oidc.ClaimActions.OfType<DeleteClaimAction>().Select(action => action.ClaimType).Should().NotContain(["acr", "auth_time", "sub"]);
    }

    // --- a sign-in that fails, and signing out

    [Theory]
    [InlineData("/auth/callback?error=access_denied")]
    [InlineData("/auth/callback?code=abc&state=forged")]
    [InlineData("/auth/callback")]
    public async Task A_sign_in_that_fails_goes_back_to_the_interface_which_says_so_and_nothing_of_the_reason_is_in_the_address(string callback)
    {
        using var factory = Factory();

        var response = await Client(factory).GetAsync(callback, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/?signInFailed=1");
    }

    [Fact]
    public async Task Signing_out_is_a_post_that_ends_the_session_and_tells_the_provider_who_leaves_and_where_to_go_back()
    {
        using var factory = Factory();
        var client = Client(factory, Operator);

        var response = await client.PostAsync("/auth/logout", content: null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.GetLeftPart(UriPartial.Path).Should().Be("https://sso.example/realms/example/logout");
        var query = System.Web.HttpUtility.ParseQueryString(response.Headers.Location.Query);
        query["client_id"].Should().Be("orchestrarr");
        query["post_logout_redirect_uri"].Should().Be("https://orchestrarr.example/");
        response.Headers.GetValues("Set-Cookie").Should().Contain(cookie => cookie.StartsWith("__Host-orchestrarr.session=;", StringComparison.Ordinal) && cookie.Contains("1970", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_link_is_not_a_sign_out()
    {
        using var factory = Factory();

        (await Client(factory, Operator).GetAsync("/auth/logout", Ct)).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await Client(factory, Operator).GetAsync("/signout-oidc", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Origin", "https://evil.example")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    public async Task Signing_out_from_another_site_is_refused(string header, string value)
    {
        using var factory = Factory();
        var client = Client(factory, Operator);
        client.DefaultRequestHeaders.Add(header, value);

        var response = await client.PostAsync("/auth/logout", content: null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_forged_host_does_not_change_where_the_provider_sends_the_person_back_to_after_signing_out()
    {
        using var factory = Factory();
        var client = Client(factory, Operator);
        client.DefaultRequestHeaders.Host = "evil.example";

        var response = await client.PostAsync("/auth/logout", content: null, Ct);

        System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query)["post_logout_redirect_uri"].Should().Be("https://orchestrarr.example/");
    }

    // --- the settings are checked when the host starts

    [Fact]
    public void A_host_with_an_unsafe_setting_does_not_start()
    {
        using var factory = Factory(settings => settings["SignIn:Authority"] = "http://sso.example/realms/example");

        var start = () => factory.CreateClient();

        start.Should().Throw<Exception>().Which.ToString().Should().Contain("SignIn:Authority");
    }

    [Fact]
    public void Turning_the_sign_in_on_without_any_setting_does_not_start_the_host()
    {
        using var factory = new ForgeApiFactory { EnvironmentName = "Testing", Settings = new Dictionary<string, string?> { ["Approvals:Mode"] = "OpenIdConnect" } };

        var start = () => factory.CreateClient();

        start.Should().Throw<Exception>().Which.ToString().Should().Contain("SignIn:");
    }

    [Fact]
    public async Task The_sandbox_has_no_sign_in_routes_and_the_probe_answers()
    {
        using var factory = new ForgeApiFactory();
        var client = factory.CreateClient();

        (await client.GetAsync("/auth/login", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/v1/system/status", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Without_the_sign_in_and_without_the_sandbox_approving_is_off_and_the_api_is_open_as_the_readme_says()
    {
        using var factory = new ApprovalsDisabledApiFactory();
        var client = factory.CreateClient();

        (await client.GetAsync("/api/v1/work-items", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/auth/login", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- who is signed in, even when the API refuses them

    [Fact]
    public async Task Who_is_signed_in_is_told_to_any_session_and_whether_the_api_will_answer_them()
    {
        using var factory = Factory(settings => settings["SignIn:ReaderGroup"] = "readers");

        var operatorMe = await Json(await Client(factory, Operator).GetAsync("/auth/me", Ct));
        var refused = await Client(factory, NotAnOperator).GetAsync("/auth/me", Ct);

        operatorMe.GetProperty("name").GetString().Should().Be("alice");
        operatorMe.GetProperty("canRead").GetBoolean().Should().BeTrue();
        operatorMe.GetProperty("isOperator").GetBoolean().Should().BeTrue();
        refused.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(refused);
        body.GetProperty("canRead").GetBoolean().Should().BeFalse();
        body.GetProperty("isOperator").GetBoolean().Should().BeFalse();
        (await Client(factory, NotAnOperator).GetAsync("/api/v1/work-items", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Without_a_session_nobody_is_anybody()
    {
        using var factory = Factory();

        var response = await Client(factory).GetAsync("/auth/me", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
    }

    // --- the interface is never framed

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/v1/system/status")]
    [InlineData("/auth/login")]
    public async Task Every_answer_forbids_being_framed_by_another_page(string path)
    {
        using var factory = Factory();

        var response = await Client(factory, Operator).GetAsync(path, Ct);

        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        response.Headers.GetValues("Content-Security-Policy").Should().Equal("frame-ancestors 'none'");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
    }

    // --- a page of another site cannot fill the cookies of the person

    [Fact]
    public async Task Past_a_number_of_sign_ins_in_flight_no_more_cookies_are_added()
    {
        using var factory = Factory();
        var client = Client(factory);
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", Enumerable.Range(0, 8).Select(n => $".AspNetCore.Correlation.state{n}=N")));

        var response = await client.GetAsync("/auth/login", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await Json(response)).GetProperty("error").GetString().Should().Be("tooManySignIns");
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    // --- the provider cannot be reached

    [Fact]
    public async Task When_the_provider_cannot_be_reached_the_person_is_told_and_nothing_of_the_failure_reaches_them()
    {
        using var factory = Factory(providerReachable: false);

        var response = await Client(factory).GetAsync("/auth/login", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/?signInFailed=1");
    }

    // --- the secret of the client stays out of any text

    [Fact]
    public void The_secret_of_the_client_is_not_in_the_text_of_the_settings()
    {
        var settings = new SignInOptions { Authority = "https://sso.example/r", ClientId = "orchestrarr", ClientSecret = "a-very-secret-value", PublicUrl = "https://orchestrarr.example" }.Validate(false);

        settings.ToString().Should().NotContain("a-very-secret-value").And.Contain("orchestrarr");
    }
}
