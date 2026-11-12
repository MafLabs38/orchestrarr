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
            var cluster = new PlatformScope("k3s", "k3s", "k3s", null, null, ["worker", "control"], ["worker", "control"], new VersionPointer("example/infra", "ansible/all.yml", "k3s_version"));
            services.AddSingleton(new DescriptionScope([], [cluster], [new RepositoryScope("example/infra", "kubernetes", ["k8s/**/*.yaml", "ansible/all.yml"])]));
            services.RemoveAll<IK3sReleases>();
            services.AddSingleton<IK3sReleases>(new FakeReleases());
            services.RemoveAll<IInfrastructureSource>();
            services.AddSingleton<IInfrastructureSource>(new FakeCode());
        });
    }

    private sealed class FakeCode : IInfrastructureSource
    {
        public Task<InfrastructureSnapshot> ReadAsync(IReadOnlyList<RepositoryScope> scope, CancellationToken cancellationToken) =>
            Task.FromResult(new InfrastructureSnapshot(DateTimeOffset.UnixEpoch,
                [new InfrastructureFile("example/infra", "k8s/cache.yaml", "kubernetes", Manifest), new InfrastructureFile("example/infra", "ansible/all.yml", "ansible-var", "k3s_version: \"v1.36.5+k3s1\"\n")], []));
    }

    private sealed class FakeReleases : IK3sReleases
    {
        private static readonly K3sReleases Known = new(K3sVersion.Parse("v1.37.2+k3s1")!, new Dictionary<int, K3sVersion> { [36] = K3sVersion.Parse("v1.36.6+k3s1")!, [37] = K3sVersion.Parse("v1.37.2+k3s1")! });

        public Task<K3sReleases?> GetAsync(CancellationToken cancellationToken) => Task.FromResult<K3sReleases?>(Known);

        public K3sReleases? Peek() => Known;
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
        traefik.GetProperty("tab").GetString().Should().Be("configure");
        var code = table.GetProperty("code");
        code.GetProperty("inScope").GetBoolean().Should().BeTrue();
        code.GetProperty("repositories").GetInt32().Should().Be(1);
        code.GetProperty("files").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task The_platform_of_the_description_has_its_rows_and_its_change_is_shown_like_an_image_s()
    {
        using var factory = new CodeApiFactory();
        using var client = await SignedIn(factory);
        var table = await Table(client);

        var patch = table.GetProperty("rows").EnumerateArray().First(row => row.GetProperty("id").GetString() == "platform:k3s:patch");
        var shown = await client.GetFromJsonAsync<JsonElement>("/api/v1/problems/change?id=platform%3Ak3s%3Apatch", Ct);

        patch.GetProperty("kind").GetString().Should().Be("platformUpdate");
        patch.GetProperty("why").GetString().Should().Be("platformUpgradeIsPlanned");
        patch.GetProperty("description").GetString().Should().Be("v1.36.5+k3s1 → v1.36.6+k3s1");
        table.GetProperty("rows").EnumerateArray().Should().Contain(row => row.GetProperty("id").GetString() == "platform:k3s:minor");
        shown.GetProperty("changes").EnumerateArray().Single().GetProperty("after").GetString().Should().Be("k3s_version: \"v1.36.6+k3s1\"");
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
