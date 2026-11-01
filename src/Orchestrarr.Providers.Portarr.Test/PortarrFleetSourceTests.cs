using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Fleet;

namespace Orchestrarr.Providers.Portarr.Test;

public sealed class PortarrFleetSourceTests
{
    private static readonly string SampleFile = Path.Combine(AppContext.BaseDirectory, "portarr-maintenance.sample.json");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task The_sample_file_is_mapped_to_the_fleet_model()
    {
        var fleet = await new SampleFleetSource(SampleFile, _time).ReadAsync(CancellationToken.None);

        fleet.ReadAt.Should().Be(_time.GetUtcNow());
        fleet.Repositories.Should().HaveCount(5);
        var shop = fleet.Repositories.Single(r => r.FullName == "example/shop");
        shop.DependencyAlerts.Should().Be(new AlertCounts(1, 2, 1, 0));
        shop.OpenDependencyPullRequests.Should().Be(5);
        shop.Ci.Should().Be(CiState.Passing);
        shop.AppIds.Should().Equal("shop");
        shop.ImageReferences.Should().Equal("ghcr.io/example/shop:1.8.2");
    }

    [Fact]
    public async Task A_failed_ci_run_and_an_archived_repository_are_mapped()
    {
        var fleet = await new SampleFleetSource(SampleFile, _time).ReadAsync(CancellationToken.None);

        fleet.Repositories.Single(r => r.FullName == "example/recipes").Ci.Should().Be(CiState.Failing);
        fleet.Repositories.Single(r => r.FullName == "example/legacy-tool").Archived.Should().BeTrue();
    }

    [Fact]
    public async Task Images_carry_their_status_their_change_and_where_they_run()
    {
        var fleet = await new SampleFleetSource(SampleFile, _time).ReadAsync(CancellationToken.None);

        var recipes = fleet.Images.Single(i => i.Reference == "ghcr.io/example/recipes:2.0.0");
        recipes.Status.Should().Be(ImageStatus.Behind);
        recipes.Change.Should().Be(ImageChange.Major);
        recipes.Latest.Should().Be("3.0.1");
        recipes.RepositoryFullName.Should().Be("example/recipes");
        recipes.Placements.Should().ContainSingle().Which.EnvironmentName.Should().Be("Server 1");
        fleet.Images.Single(i => i.Reference == "postgres:16").Status.Should().Be(ImageStatus.Floating);
    }

    [Fact]
    public async Task A_missing_optional_part_of_the_report_is_read_as_nothing_wrong()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orchestrarr-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{ "repositories": [ { "fullName": "example/bare" } ] }""", TestContext.Current.CancellationToken);
        try
        {
            var fleet = await new SampleFleetSource(path, _time).ReadAsync(CancellationToken.None);

            var bare = fleet.Repositories.Should().ContainSingle().Which;
            bare.DependencyAlerts.Should().Be(AlertCounts.None);
            bare.OpenDependencyPullRequests.Should().Be(0);
            bare.Ci.Should().Be(CiState.Unknown);
            fleet.Images.Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Portarr_is_read_at_its_maintenance_route_with_the_api_key_header()
    {
        var handler = new StubHandler("""{ "repositories": [ { "fullName": "example/app", "dependabotPullRequests": 2 } ] }""");
        var keyVariable = $"ORCHESTRARR_TEST_KEY_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(keyVariable, "ptr_secret");
        try
        {
            var provider = Provider(new Dictionary<string, string?> { ["Portarr:BaseUrl"] = "http://portarr.example:8484", ["Portarr:ApiKeyEnv"] = keyVariable }, handler);

            var source = provider.GetRequiredService<IFleetSource>();
            var fleet = await source.ReadAsync(CancellationToken.None);

            source.Kind.Should().Be("portarr");
            fleet.Repositories.Should().ContainSingle().Which.OpenDependencyPullRequests.Should().Be(2);
            handler.Request!.RequestUri.Should().Be(new Uri("http://portarr.example:8484/api/v1/maintenance"));
            handler.Request.Headers.GetValues(PortarrFleetSource.ApiKeyHeader).Should().Equal("ptr_secret");
            handler.Request.Method.Should().Be(HttpMethod.Get);
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyVariable, null);
        }
    }

    [Fact]
    public async Task Portarr_refusing_the_key_is_an_error_not_an_empty_fleet()
    {
        var handler = new StubHandler("{}", HttpStatusCode.Unauthorized);
        var provider = Provider(new Dictionary<string, string?> { ["Portarr:BaseUrl"] = "http://portarr.example:8484" }, handler);

        var read = () => provider.GetRequiredService<IFleetSource>().ReadAsync(CancellationToken.None);

        await read.Should().ThrowAsync<HttpRequestException>();
    }

    [Theory]
    [InlineData("http://portarr.example", null, "portarr")]
    [InlineData(null, "sample.json", "sample")]
    [InlineData(null, null, "none")]
    public void The_source_follows_the_configuration(string? baseUrl, string? sampleFile, string expectedKind)
    {
        var provider = Provider(new Dictionary<string, string?> { ["Portarr:BaseUrl"] = baseUrl, ["Portarr:SampleFile"] = sampleFile }, new StubHandler("{}"));

        provider.GetRequiredService<IFleetSource>().Kind.Should().Be(expectedKind);
    }

    [Fact]
    public void A_portarr_url_wins_over_a_sample_file()
    {
        var provider = Provider(new Dictionary<string, string?> { ["Portarr:BaseUrl"] = "http://portarr.example", ["Portarr:SampleFile"] = "sample.json" }, new StubHandler("{}"));

        provider.GetRequiredService<IFleetSource>().Kind.Should().Be("portarr");
    }

    private ServiceProvider Provider(Dictionary<string, string?> settings, StubHandler handler)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddPortarrFleetSource(configuration, AppContext.BaseDirectory);
        services.AddHttpClient(PortarrFleetSource.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
        }
    }
}
