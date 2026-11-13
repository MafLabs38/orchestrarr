using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orchestrarr.Core.Infrastructure;
using Orchestrarr.Host.Decisions;

namespace Orchestrarr.Host.Test;

/// <summary>The table host with a description that names one repository, and the code it holds (ADR 0028).</summary>
public sealed class CodeApiFactory : TableApiFactory
{
    public const string Manifest = "containers:\n  - image: redis:7.2.3\n  - image: grafana/grafana:11.2.0 # {\"$imagepolicy\": \"flux-system:grafana\"}\n  - image: postgres:16\n";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DescriptionScope>();
            services.AddSingleton(new DescriptionScope([], [new RepositoryScope("example/infra", "kubernetes", ["k8s/**/*.yaml"])]));
            services.RemoveAll<IInfrastructureSource>();
            services.AddSingleton<IInfrastructureSource>(new FakeCode());
        });
    }

    private sealed class FakeCode : IInfrastructureSource
    {
        public Task<InfrastructureSnapshot> ReadAsync(IReadOnlyList<RepositoryScope> scope, CancellationToken cancellationToken) =>
            Task.FromResult(new InfrastructureSnapshot(DateTimeOffset.UnixEpoch,
                [new InfrastructureFile("example/infra", "k8s/cache.yaml", "kubernetes", Manifest)], []));
    }
}

public sealed class CodeApiTests
{
    private const string Header = "X-Orchestrarr-Request";
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static async Task<HttpClient> SignedIn(TableApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Header, "test");
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { name = AccountsApiFactory.AdminName, password = AccountsApiFactory.AdminPassword }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<JsonElement> Table(HttpClient client)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var table = await client.GetFromJsonAsync<JsonElement>("/api/v1/problems", Ct);
            if (table.GetProperty("rows").EnumerateArray().All(row => row.GetProperty("kind").GetString() != "pullRequestsNotRead"))
            {
                return table;
            }
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException("The table was never whole.");
    }

    private static JsonElement ImageRow(JsonElement table, string reference) =>
        table.GetProperty("rows").EnumerateArray().First(row => row.GetProperty("description").GetString()!.StartsWith(reference, StringComparison.Ordinal));

    [Fact]
    public async Task An_image_row_says_where_its_version_is_written_what_the_change_would_write_and_what_is_not_in_the_scope()
    {
        using var factory = new CodeApiFactory();
        using var client = await SignedIn(factory);

        var table = await Table(client);

        var redis = ImageRow(table, "redis:7.2.3");
        redis.GetProperty("why").GetString().Should().Be("codeChangeShown");
        redis.GetProperty("proposedTag").GetString().Should().Be("7.2.6");
        redis.GetProperty("locations").EnumerateArray().Single().GetProperty("path").GetString().Should().Be("k8s/cache.yaml");
        ImageRow(table, "grafana/grafana:11.2.0").GetProperty("why").GetString().Should().Be("managedByAutomation");
        var traefik = ImageRow(table, "ghcr.io/example/api-gateway:3.1.0");
        traefik.GetProperty("why").GetString().Should().Be("notInScope");
        var code = table.GetProperty("code");
        code.GetProperty("inScope").GetBoolean().Should().BeTrue();
        code.GetProperty("repositories").GetInt32().Should().Be(1);
        code.GetProperty("files").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task The_change_is_shown_as_a_diff_for_a_row_that_has_one_and_refused_for_the_others()
    {
        using var factory = new CodeApiFactory();
        using var client = await SignedIn(factory);
        var table = await Table(client);

        var shown = await client.GetFromJsonAsync<JsonElement>($"/api/v1/problems/change?id={Uri.EscapeDataString(ImageRow(table, "redis:7.2.3").GetProperty("id").GetString()!)}", Ct);
        var managed = await client.GetAsync($"/api/v1/problems/change?id={Uri.EscapeDataString(ImageRow(table, "grafana/grafana:11.2.0").GetProperty("id").GetString()!)}", Ct);
        var unknown = await client.GetAsync("/api/v1/problems/change?id=nothing", Ct);

        shown.GetProperty("tag").GetString().Should().Be("7.2.6");
        var change = shown.GetProperty("changes").EnumerateArray().Single();
        change.GetProperty("after").GetString().Should().Contain("redis:7.2.6");
        change.GetProperty("diff").GetString().Should().Contain("-  - image: redis:7.2.3\n+  - image: redis:7.2.6");
        managed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_a_scope_the_table_says_nothing_about_the_code_and_the_change_is_refused()
    {
        using var factory = new TableApiFactory();
        using var client = await SignedIn(factory);

        var table = await Table(client);

        table.GetProperty("code").GetProperty("inScope").GetBoolean().Should().BeFalse();
        ImageRow(table, "redis:7.2.3").GetProperty("why").GetString().Should().NotBe("codeChangeShown");
        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync("/api/v1/problems/change?id=x", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
