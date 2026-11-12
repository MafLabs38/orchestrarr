using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Host.Test;

/// <summary>The host with an icon source that knows one icon, as Portarr does.</summary>
public sealed class IconApiFactory : AccountsApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IIconSource>(new OneIcon());
            // The icons relayed are those the source named for its applications: this one names jellyfin, and the test below asks for names it did not give
            services.RemoveAll<IFleetSource>();
            services.AddSingleton<IFleetSource>(new OneApplication());
        });
    }

    private sealed class OneApplication : IFleetSource
    {
        public string Kind => "test";
        public bool Configured => true;

        public Task<FleetSnapshot> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new FleetSnapshot(DateTimeOffset.UnixEpoch, [], [], [new ApplicationInfo("app-1", "Jellyfin", "jellyfin")]));
    }

    private sealed class OneIcon : IIconSource
    {
        public Task<IconImage?> GetAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(name is "jellyfin" or "unlisted" ? new IconImage("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray(), "image/svg+xml") : null);
    }
}

public sealed class IconApiTests
{
    private const string Header = "X-Orchestrarr-Request";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static async Task<HttpClient> SignedIn(IconApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, "test");
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { name = AccountsApiFactory.AdminName, password = AccountsApiFactory.AdminPassword }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task An_icon_is_served_as_a_picture_that_cannot_run_anything_and_only_to_someone_signed_in()
    {
        using var factory = new IconApiFactory();
        using var signedIn = await SignedIn(factory);
        using var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add(Header, "test");

        var response = await signedIn.GetAsync("/api/v1/icons/jellyfin", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/svg+xml");
        response.Headers.GetValues("Content-Security-Policy").Single().Should().Be("default-src 'none'; sandbox");
        response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        (await anonymous.GetAsync("/api/v1/icons/jellyfin", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("..%2Fsecrets")]
    [InlineData(".hidden")]
    [InlineData("a%20b")]
    public async Task An_unknown_icon_or_a_name_that_is_not_a_plain_name_is_not_found(string name)
    {
        using var factory = new IconApiFactory();
        using var signedIn = await SignedIn(factory);

        (await signedIn.GetAsync($"/api/v1/icons/{name}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_icon_the_source_did_not_name_for_an_application_is_not_relayed_even_when_the_source_has_it()
    {
        using var factory = new IconApiFactory();
        using var signedIn = await SignedIn(factory);

        (await signedIn.GetAsync("/api/v1/icons/unlisted", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_host_without_icons_answers_not_found()
    {
        using var factory = new AccountsApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, "test");
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { name = AccountsApiFactory.AdminName, password = AccountsApiFactory.AdminPassword }, Ct)).EnsureSuccessStatusCode();

        (await client.GetAsync("/api/v1/icons/jellyfin", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
