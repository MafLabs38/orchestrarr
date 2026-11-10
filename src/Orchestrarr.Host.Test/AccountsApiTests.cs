using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Host.Test;

/// <summary>The real sign-in: no test filter, the cookie, the server-side session, the roles and the accounts (ADR 0018).</summary>
public class AccountsApiFactory : WebApplicationFactory<Program>
{
    public const string AdminName = "root";
    public const string AdminPassword = "the first password of root";

    private readonly string _folder = Directory.CreateTempSubdirectory("orchestrarr-auth").FullName;

    public AccountsApiFactory()
    {
        File.WriteAllText(Path.Combine(_folder, "admin-password"), AdminPassword);
        // A built interface: its page, and a file with an extension
        Directory.CreateDirectory(Path.Combine(_folder, "wwwroot", "assets"));
        File.WriteAllText(Path.Combine(_folder, "wwwroot", "index.html"), "<!doctype html><title>Orchestrarr</title>");
        File.WriteAllText(Path.Combine(_folder, "wwwroot", "assets", "app.js"), "console.log('app');");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseWebRoot(Path.Combine(_folder, "wwwroot"));
        builder.UseSetting(FleetSourceFactory.SampleFileKey, Path.Combine(AppContext.BaseDirectory, "portarr-maintenance.sample.json"));
        builder.UseSetting("Storage:Provider", "Memory");
        builder.UseSetting("Storage:MemoryStartsEngaged", "false");
        builder.UseSetting("Auth:AdminUser", AdminName);
        builder.UseSetting("Auth:AdminPasswordFile", Path.Combine(_folder, "admin-password"));
        // The test server speaks plain HTTP, unless a test asks for the production setting
        builder.UseSetting("Auth:CookieSecurity", CookieSecurity);
        // A cheap hasher: the tests check what is done with a hash, not how slow it is to make
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IPasswordHasher<Account>>(new PasswordHasher<Account>(Options.Create(new PasswordHasherOptions { IterationCount = 1000 }))));
    }

    protected virtual string CookieSecurity => "SameAsRequest";

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // A factory can be disposed twice (a test that disposes it and the host that does): the second time there is nothing left
        if (disposing && Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}

public sealed class AccountsApiTests : IClassFixture<AccountsApiFactory>
{
    private const string Header = "X-Orchestrarr-Request";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly AccountsApiFactory _factory;

    public AccountsApiTests(AccountsApiFactory factory)
    {
        _factory = factory;
    }

    // One client is one browser: it has its own cookies
    private HttpClient Browser(bool withHeader = true)
    {
        var client = _factory.CreateClient();
        if (withHeader)
        {
            client.DefaultRequestHeaders.Add(Header, "test");
        }

        return client;
    }

    private static Task<HttpResponseMessage> Login(HttpClient client, string name, string password) =>
        client.PostAsJsonAsync("/api/v1/auth/login", new { name, password }, Ct);

    private async Task<HttpClient> SignedIn(string name, string password)
    {
        var client = Browser();
        (await Login(client, name, password)).StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    private Task<HttpClient> Admin() => SignedIn(AccountsApiFactory.AdminName, AccountsApiFactory.AdminPassword);

    private async Task<(string Name, string Password)> NewAccount(string role)
    {
        var name = $"{role.ToLowerInvariant()}-{Guid.NewGuid().ToString("N")[..8]}";
        var password = $"password of {name}";
        using var admin = await Admin();
        (await admin.PostAsJsonAsync("/api/v1/accounts", new { name, role, password }, Ct)).StatusCode.Should().Be(HttpStatusCode.Created);
        return (name, password);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    // --- who is let in

    [Theory]
    [InlineData("/api/v1/work-items")]
    [InlineData("/api/v1/system/status")]
    [InlineData("/api/v1/approvals")]
    [InlineData("/api/v1/audit")]
    [InlineData("/api/v1/runs")]
    [InlineData("/api/v1/accounts")]
    [InlineData("/api/v1/auth/me")]
    [InlineData("/api/v1/nothing-here")]
    public async Task Nobody_signed_in_reads_nothing_and_is_never_redirected(string route)
    {
        var response = await Browser().GetAsync(route, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_route_of_the_api_is_a_404_for_a_person_who_signed_in_and_never_the_page_of_the_interface()
    {
        using var admin = await Admin();

        var response = await admin.GetAsync("/api/v1/nothing-here", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_probe_of_the_deployment_is_public()
    {
        (await Browser().GetAsync("/health", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/assets/app.js")]
    [InlineData("/runs")]
    [InlineData("/system/accounts")]
    public async Task The_page_and_the_files_of_the_interface_load_before_anybody_signs_in_or_there_would_be_no_sign_in_to_show(string path)
    {
        var response = await Browser().GetAsync(path, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_right_password_signs_in_and_the_cookie_is_http_only_and_strict()
    {
        var client = Browser();

        var response = await Login(client, AccountsApiFactory.AdminName, AccountsApiFactory.AdminPassword);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var me = await Json(response);
        me.GetProperty("name").GetString().Should().Be("root");
        me.GetProperty("role").GetString().Should().Be(Roles.Admin);
        me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Should().BeEquivalentTo(Permissions.All);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("orchestrarr.session=", StringComparison.Ordinal));
        cookie.Should().Contain("httponly").And.Contain("samesite=strict");
        (await client.GetAsync("/api/v1/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/work-items", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_name_get_the_same_answer()
    {
        var wrong = await Login(Browser(), AccountsApiFactory.AdminName, "not the password");
        var unknown = await Login(Browser(), "nobody-here", "not the password");

        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrong.Content.ReadAsStringAsync(Ct)).Should().Be(await unknown.Content.ReadAsStringAsync(Ct));
        wrong.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task A_sign_in_without_a_name_or_a_password_is_a_400()
    {
        (await Browser().PostAsJsonAsync("/api/v1/auth/login", new { name = "root" }, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Signing_out_ends_the_session_on_the_server_so_a_copied_cookie_is_dead()
    {
        var client = Browser();
        var response = await Login(client, AccountsApiFactory.AdminName, AccountsApiFactory.AdminPassword);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("orchestrarr.session=", StringComparison.Ordinal)).Split(';')[0];

        (await client.PostAsync("/api/v1/auth/logout", null, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await client.GetAsync("/api/v1/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var replay = Browser();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Add("Cookie", cookie);
        (await replay.SendAsync(request, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_write_from_a_person_needs_the_header_that_only_the_interface_sends()
    {
        var response = await Browser(withHeader: false).PostAsJsonAsync("/api/v1/auth/login", new { name = "root", password = AccountsApiFactory.AdminPassword }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Theory]
    [InlineData("/API/v1/auth/login")]
    [InlineData("/api/V1/AUTH/login")]
    public async Task The_header_is_asked_whatever_the_case_of_the_path(string path)
    {
        // Routing ignores the case of a path: the check of the header must not
        var response = await Browser(withHeader: false).PostAsJsonAsync(path, new { name = "root", password = AccountsApiFactory.AdminPassword }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_name_and_the_answer_says_when_to_come_back()
    {
        var (name, password) = await NewAccount(Roles.Reader);
        var client = Browser();
        for (var attempt = 0; attempt < LoginThrottle.MaxFailures; attempt++)
        {
            (await Login(client, name, "wrong password")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        var locked = await Login(client, name, password);

        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        locked.Headers.RetryAfter.Should().NotBeNull();
        (await Json(locked)).GetProperty("error").GetString().Should().Be("locked");
    }

    // --- what each role may do

    [Fact]
    public async Task A_reader_reads_everything_and_changes_nothing()
    {
        var (name, password) = await NewAccount(Roles.Reader);
        using var reader = await SignedIn(name, password);

        (await reader.GetAsync("/api/v1/work-items", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.GetAsync("/api/v1/audit", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.GetAsync("/api/v1/system/status", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await reader.PostAsJsonAsync("/api/v1/approvals", new { workItemId = "repository:example/shop" }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reader.GetAsync("/api/v1/accounts", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_operator_works_the_global_stop_but_does_not_manage_accounts()
    {
        var (name, password) = await NewAccount(Roles.Operator);
        using var operatorClient = await SignedIn(name, password);

        (await operatorClient.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await operatorClient.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = false }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await operatorClient.GetAsync("/api/v1/accounts", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var status = (await Json(await operatorClient.GetAsync("/api/v1/system/status", Ct))).GetProperty("approvals");
        status.GetProperty("isOperator").GetBoolean().Should().BeTrue();
        status.GetProperty("operator").GetString().Should().Be((await Json(await operatorClient.GetAsync("/api/v1/auth/me", Ct))).GetProperty("subject").GetString());
    }

    // --- the accounts

    [Fact]
    public async Task The_administrator_creates_changes_and_removes_accounts_and_every_change_is_traced()
    {
        using var admin = await Admin();
        var name = $"carol-{Guid.NewGuid().ToString("N")[..6]}";

        var created = await admin.PostAsJsonAsync("/api/v1/accounts", new { name, role = Roles.Reader, password = "a long enough password" }, Ct);
        var account = await Json(created);
        var id = account.GetProperty("id").GetGuid();
        var duplicate = await admin.PostAsJsonAsync("/api/v1/accounts", new { name = name.ToUpperInvariant(), role = Roles.Reader, password = "a long enough password" }, Ct);
        var weak = await admin.PostAsJsonAsync("/api/v1/accounts", new { name = name + "x", role = Roles.Reader, password = "short" }, Ct);
        var promoted = await admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = Roles.Operator }, Ct);
        var removed = await admin.DeleteAsync($"/api/v1/accounts/{id}", Ct);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        weak.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(weak)).GetProperty("error").GetString().Should().Be("invalid");
        promoted.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(promoted)).GetProperty("role").GetString().Should().Be(Roles.Operator);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var trail = await Json(await admin.GetAsync("/api/v1/audit?limit=200", Ct));
        trail.EnumerateArray().Select(entry => entry.GetProperty("event").GetString()).Should().Contain([AccountEvents.AccountCreated, AccountEvents.AccountRoleChanged, AccountEvents.AccountDeleted, AccountEvents.LoginSucceeded]);
    }

    [Fact]
    public async Task The_last_administrator_cannot_be_removed_demoted_or_disabled_through_the_api()
    {
        using var admin = await Admin();
        var me = await Json(await admin.GetAsync("/api/v1/auth/me", Ct));
        var accounts = await Json(await admin.GetAsync("/api/v1/accounts", Ct));
        var id = accounts.EnumerateArray().Single(a => a.GetProperty("name").GetString() == "root").GetProperty("id").GetGuid();
        me.GetProperty("role").GetString().Should().Be(Roles.Admin);

        (await admin.DeleteAsync($"/api/v1/accounts/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = Roles.Reader }, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { enabled = false }, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_disabled_account_loses_its_session_at_once_and_a_changed_role_applies_at_once()
    {
        var (name, password) = await NewAccount(Roles.Operator);
        using var person = await SignedIn(name, password);
        using var admin = await Admin();
        var id = (await Json(await admin.GetAsync("/api/v1/accounts", Ct))).EnumerateArray().Single(a => a.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();

        await admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = Roles.Reader }, Ct);
        (await person.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { enabled = false }, Ct);
        (await person.GetAsync("/api/v1/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Login(Browser(), name, password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Changing_ones_own_password_needs_the_current_one_and_ends_the_other_sessions()
    {
        var (name, password) = await NewAccount(Roles.Reader);
        using var mine = await SignedIn(name, password);
        using var elsewhere = await SignedIn(name, password);

        var wrong = await mine.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = "not mine", newPassword = "a brand new password" }, Ct);
        var weak = await mine.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = password, newPassword = "short" }, Ct);
        var changed = await mine.PostAsJsonAsync("/api/v1/auth/password", new { currentPassword = password, newPassword = "a brand new password" }, Ct);

        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        weak.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        changed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await mine.GetAsync("/api/v1/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await elsewhere.GetAsync("/api/v1/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Login(Browser(), name, password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Login(Browser(), name, "a brand new password")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_roles_are_listed_for_the_administrator_with_what_each_allows()
    {
        using var admin = await Admin();

        var roles = await Json(await admin.GetAsync("/api/v1/roles", Ct));

        roles.EnumerateArray().Select(role => role.GetProperty("name").GetString()).Should().Equal(Roles.Admin, Roles.Operator, Roles.Reader);
    }

    // --- the header, with a session behind it

    [Theory]
    [InlineData("POST", "/api/v1/approvals")]
    [InlineData("PUT", "/api/v1/safety/kill-switch")]
    [InlineData("POST", "/api/v1/accounts")]
    [InlineData("PUT", "/api/v1/accounts/6f1d2a52-0d0f-4f4e-9a52-6d2d0c9a3f10")]
    [InlineData("DELETE", "/api/v1/accounts/6f1d2a52-0d0f-4f4e-9a52-6d2d0c9a3f10")]
    [InlineData("POST", "/api/v1/auth/password")]
    [InlineData("POST", "/api/v1/auth/logout")]
    public async Task A_write_from_a_signed_in_person_without_the_header_is_refused_and_the_session_stays(string method, string route)
    {
        using var admin = await Admin();
        admin.DefaultRequestHeaders.Remove(Header);

        using var request = new HttpRequestMessage(new HttpMethod(method), route) { Content = JsonContent.Create(new { }) };
        var response = await admin.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        admin.DefaultRequestHeaders.Add(Header, "test");
        (await admin.GetAsync("/api/v1/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "a refused sign-out forged from another page must not end the session");
    }

    // --- who may do what

    [Theory]
    [InlineData(Roles.Reader, "POST", "/api/v1/approvals", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Reader, "PUT", "/api/v1/safety/kill-switch", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Reader, "GET", "/api/v1/runs", HttpStatusCode.OK)]
    [InlineData(Roles.Reader, "GET", "/api/v1/approvals", HttpStatusCode.OK)]
    [InlineData(Roles.Reader, "GET", "/api/v1/audit", HttpStatusCode.OK)]
    [InlineData(Roles.Reader, "GET", "/api/v1/accounts", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Reader, "GET", "/api/v1/roles", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Operator, "GET", "/api/v1/runs", HttpStatusCode.OK)]
    [InlineData(Roles.Operator, "GET", "/api/v1/roles", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Operator, "POST", "/api/v1/accounts", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Operator, "PUT", "/api/v1/accounts/6f1d2a52-0d0f-4f4e-9a52-6d2d0c9a3f10", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Operator, "DELETE", "/api/v1/accounts/6f1d2a52-0d0f-4f4e-9a52-6d2d0c9a3f10", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Operator, "POST", "/api/v1/accounts/6f1d2a52-0d0f-4f4e-9a52-6d2d0c9a3f10/password", HttpStatusCode.Forbidden)]
    public async Task Each_role_does_only_what_its_permissions_allow(string role, string method, string route, HttpStatusCode expected)
    {
        var (name, password) = await NewAccount(role);
        using var person = await SignedIn(name, password);

        using var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method != "GET")
        {
            request.Content = JsonContent.Create(new { });
        }
        var response = await person.SendAsync(request, Ct);

        response.StatusCode.Should().Be(expected);
    }

    [Fact]
    public void Every_route_that_changes_something_asks_for_a_permission_or_is_on_the_list_of_the_few_that_do_not()
    {
        // The few writes open to any signed-in person, or to nobody signed in (the executors carry a token of their own): a new write route
        // that nobody thought about must not be one of them by omission, so it has to be added here, on purpose.
        string[] open = ["/api/v1/auth/login", "/api/v1/auth/logout", "/api/v1/auth/password"];
        var routes = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) == true)
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Any(method => method != "GET") == true)
            .ToList();

        routes.Should().NotBeEmpty();
        var unguarded = routes
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null && !open.Contains(endpoint.RoutePattern.RawText!, StringComparer.OrdinalIgnoreCase))
            .Where(endpoint => !endpoint.Metadata.OfType<IAuthorizeData>().Any(data => !string.IsNullOrEmpty(data.Policy)))
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();
        unguarded.Should().BeEmpty("a route that changes something needs the permission that allows it");
    }

    // --- an administrator and the accounts of others

    private static async Task<Guid> IdOf(HttpClient admin, string name)
    {
        var accounts = await Json(await admin.GetAsync("/api/v1/accounts", Ct));
        return accounts.EnumerateArray().Single(account => account.GetProperty("name").GetString() == name).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task An_administrator_who_sets_the_password_of_someone_ends_their_sessions_and_a_weak_one_is_refused()
    {
        var (name, password) = await NewAccount(Roles.Reader);
        using var theirs = await SignedIn(name, password);
        using var admin = await Admin();
        var id = await IdOf(admin, name);

        var weak = await admin.PostAsJsonAsync($"/api/v1/accounts/{id}/password", new { password = "short" }, Ct);
        var set = await admin.PostAsJsonAsync($"/api/v1/accounts/{id}/password", new { password = "a password set by the administrator" }, Ct);

        weak.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        set.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await theirs.GetAsync("/api/v1/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Login(Browser(), name, password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Login(Browser(), name, "a password set by the administrator")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_administrator_cannot_set_their_own_password_without_the_current_one()
    {
        using var admin = await Admin();
        var me = await Json(await admin.GetAsync("/api/v1/auth/me", Ct));

        var response = await admin.PostAsJsonAsync($"/api/v1/accounts/{me.GetProperty("subject").GetString()}/password", new { password = "a password that is not asked for" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await admin.GetAsync("/api/v1/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_deleted_account_loses_its_session_at_once_and_an_unknown_account_is_a_404()
    {
        var (name, password) = await NewAccount(Roles.Operator);
        using var theirs = await SignedIn(name, password);
        using var admin = await Admin();
        var id = await IdOf(admin, name);

        (await admin.DeleteAsync($"/api/v1/accounts/{id}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await theirs.GetAsync("/api/v1/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var unknown = Guid.NewGuid();
        (await admin.DeleteAsync($"/api/v1/accounts/{unknown}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PutAsJsonAsync($"/api/v1/accounts/{unknown}", new { role = Roles.Reader }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Changing_the_role_leaves_the_state_alone_and_the_other_way_round()
    {
        var (name, _) = await NewAccount(Roles.Reader);
        using var admin = await Admin();
        var id = await IdOf(admin, name);

        // Each request carries only the field that changed: the other is kept as it is now
        await admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { enabled = false }, Ct);
        var changed = await Json(await admin.PutAsJsonAsync($"/api/v1/accounts/{id}", new { role = Roles.Operator }, Ct));

        changed.GetProperty("role").GetString().Should().Be(Roles.Operator);
        changed.GetProperty("enabled").GetBoolean().Should().BeFalse();
    }

    // --- the garbage and the production cookie

    [Fact]
    public async Task Every_answer_forbids_being_framed_and_sniffed_and_keeps_the_page_to_itself()
    {
        var response = await Browser().GetAsync("/health", Ct);

        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("Content-Security-Policy").Single().Should().Contain("default-src 'self'").And.Contain("frame-ancestors 'none'").And.Contain("form-action 'self'");
        response.Headers.Contains("Strict-Transport-Security").Should().BeFalse("the sandbox cookie setting is not https");
    }

    [Fact]
    public async Task A_read_never_needs_the_header_of_the_interface()
    {
        using var admin = await Admin();
        admin.DefaultRequestHeaders.Remove(Header);

        (await admin.GetAsync("/api/v1/auth/me", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_cookie_that_was_made_up_is_not_a_session()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        request.Headers.Add("Cookie", "orchestrarr.session=CfDJ8-not-a-ticket; __Host-orchestrarr.session=CfDJ8-not-a-ticket");

        (await Browser().SendAsync(request, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

/// <summary>The production setting of the cookie: <c>Always</c>, so <c>Secure</c> and named with the <c>__Host-</c> prefix.</summary>
public sealed class SecureCookieFactory : AccountsApiFactory
{
    protected override string CookieSecurity => "Always";
}

public sealed class SecureCookieTests : IClassFixture<SecureCookieFactory>
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly SecureCookieFactory _factory;

    public SecureCookieTests(SecureCookieFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task By_default_the_cookie_is_secure_has_the_host_prefix_no_domain_and_the_root_path()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Orchestrarr-Request", "test");

        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { name = AccountsApiFactory.AdminName, password = AccountsApiFactory.AdminPassword }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.Contains("orchestrarr.session=", StringComparison.Ordinal));
        cookie.Should().StartWith("__Host-orchestrarr.session=");
        response.Headers.GetValues("Strict-Transport-Security").Should().ContainSingle().Which.Should().StartWith("max-age=");
        cookie.ToLowerInvariant().Should().Contain("secure").And.Contain("path=/").And.Contain("httponly").And.Contain("samesite=strict").And.NotContain("domain=");
    }
}
