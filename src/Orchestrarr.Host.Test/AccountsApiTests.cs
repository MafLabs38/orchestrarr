using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Host.Test;

/// <summary>The real sign-in: no test filter, the cookie, the server-side session, the roles and the accounts (ADR 0018).</summary>
public sealed class AccountsApiFactory : WebApplicationFactory<Program>
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
        // The test server speaks plain HTTP
        builder.UseSetting("Auth:CookieSecurity", "SameAsRequest");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
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
}
