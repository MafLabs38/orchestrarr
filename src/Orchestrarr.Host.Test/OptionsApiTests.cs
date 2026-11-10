using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Orchestrarr.Host.Settings;

namespace Orchestrarr.Host.Test;

/// <summary>The host as a deployment would start it for tests: it lets an administrator turn the code of the authenticator off (ADR 0023).</summary>
public sealed class RelaxableApiFactory : AccountsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Options:AllowRelaxingSecurity", "true");
    }
}

public sealed class OptionsApiTests : IClassFixture<AccountsApiFactory>, IClassFixture<RelaxableApiFactory>
{
    private const string Header = "X-Orchestrarr-Request";
    private const string Id = "approvals.require-second-factor";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly AccountsApiFactory _locked;
    private readonly RelaxableApiFactory _relaxable;

    public OptionsApiTests(AccountsApiFactory locked, RelaxableApiFactory relaxable)
    {
        _locked = locked;
        _relaxable = relaxable;
    }

    private static async Task<HttpClient> SignedIn(AccountsApiFactory factory, string name, string password)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, "test");
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { name, password }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        return client;
    }

    private static Task<HttpClient> Admin(AccountsApiFactory factory) => SignedIn(factory, AccountsApiFactory.AdminName, AccountsApiFactory.AdminPassword);

    private static async Task<HttpClient> NewSignedIn(AccountsApiFactory factory, string role)
    {
        var name = $"{role.ToLowerInvariant()}-{Guid.NewGuid().ToString("N")[..8]}";
        var password = $"password of {name}";
        using var admin = await Admin(factory);
        (await admin.PostAsJsonAsync("/api/v1/accounts", new { name, role, password }, Ct)).StatusCode.Should().Be(HttpStatusCode.Created);
        return await SignedIn(factory, name, password);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static Task<HttpResponseMessage> Put(HttpClient client, bool value, string? password = null) =>
        client.PutAsJsonAsync($"/api/v1/options/{Id}", new { value, password }, Ct);

    private static async Task<bool> StatusRequiresCode(HttpClient client) =>
        (await Json(await client.GetAsync("/api/v1/system/status", Ct))).GetProperty("approvals").GetProperty("requiresSecondFactor").GetBoolean();

    [Fact]
    public async Task Anybody_who_signed_in_reads_the_options_and_a_locked_host_says_why_it_cannot_be_turned_off()
    {
        using var reader = await NewSignedIn(_locked, "Reader");

        var options = await Json(await reader.GetAsync("/api/v1/options", Ct));

        var option = options.EnumerateArray().Single();
        option.GetProperty("id").GetString().Should().Be(Id);
        option.GetProperty("value").GetBoolean().Should().BeTrue();
        option.GetProperty("canTurnOff").GetBoolean().Should().BeFalse();
        option.GetProperty("lockedReason").GetString().Should().Be("host");
    }

    [Fact]
    public async Task Nobody_signed_out_reads_or_changes_the_options()
    {
        using var anonymous = _locked.CreateClient();
        anonymous.DefaultRequestHeaders.Add(Header, "test");

        (await anonymous.GetAsync("/api/v1/options", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Put(anonymous, false)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_locked_host_refuses_to_turn_the_code_off_even_to_an_administrator_with_the_right_password()
    {
        using var admin = await Admin(_locked);

        var answer = await Put(admin, false, AccountsApiFactory.AdminPassword);

        answer.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(answer)).GetProperty("error").GetString().Should().Be("lockedByHost");
        (await StatusRequiresCode(admin)).Should().BeTrue();
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("Reader")]
    public async Task Only_an_administrator_changes_an_option(string role)
    {
        using var client = await NewSignedIn(_relaxable, role);

        (await Put(client, false, "whatever")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StatusRequiresCode(client)).Should().BeTrue();
    }

    [Fact]
    public async Task Turning_the_code_off_needs_the_password_of_the_administrator_and_is_traced()
    {
        using var admin = await Admin(_relaxable);

        var missing = await Put(admin, false);
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(missing)).GetProperty("error").GetString().Should().Be("passwordRequired");
        (await Put(admin, false, "not the password")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await StatusRequiresCode(admin)).Should().BeTrue("a refused attempt changes nothing");

        var done = await Put(admin, false, AccountsApiFactory.AdminPassword);

        done.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusRequiresCode(admin)).Should().BeFalse();
        (await Json(await admin.GetAsync("/api/v1/audit?limit=50", Ct))).EnumerateArray()
            .Should().Contain(entry => entry.GetProperty("event").GetString() == "option.changed" && entry.GetProperty("detail").GetString()!.Contains("off", StringComparison.Ordinal));

        // Turning it back on lowers nothing: no password asked
        (await Put(admin, true)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusRequiresCode(admin)).Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_option_or_a_request_without_a_value_is_refused()
    {
        using var admin = await Admin(_relaxable);

        (await admin.PutAsJsonAsync("/api/v1/options/nothing.here", new { value = true }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PutAsJsonAsync($"/api/v1/options/{Id}", new { password = "x" }, Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void The_host_cannot_be_started_in_production_with_the_leave_to_lower_its_security()
    {
        var production = () => new SecurityOptionsConfiguration { AllowRelaxingSecurity = true }.Validate(production: true);

        production.Should().Throw<InvalidOperationException>().WithMessage("*production*");
        new SecurityOptionsConfiguration { AllowRelaxingSecurity = true }.Validate(production: false).AllowRelaxingSecurity.Should().BeTrue();
        new SecurityOptionsConfiguration().Validate(production: true).AllowRelaxingSecurity.Should().BeFalse("the default is locked");
    }
}
