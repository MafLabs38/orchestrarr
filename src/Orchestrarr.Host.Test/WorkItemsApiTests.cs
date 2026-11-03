using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Orchestrarr.Host.Test;

public sealed class WorkItemsApiTests : IClassFixture<SampleApiFactory>
{
    private readonly SampleApiFactory _factory;
    private readonly HttpClient _client;

    public WorkItemsApiTests(SampleApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static readonly string[] SeverityOrder = ["danger", "warning", "info"];

    private Task<JsonElement> GetAsync(string path) => _client.GetFromJsonAsync<JsonElement>(path, TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_work_items_come_most_severe_first_with_camel_case_names_and_enums_as_text()
    {
        var report = await GetAsync("/api/v1/work-items");

        report.GetProperty("sourceKind").GetString().Should().Be("sample");
        report.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        var severities = report.GetProperty("items").EnumerateArray().Select(i => Array.IndexOf(SeverityOrder, i.GetProperty("severity").GetString())).ToList();
        severities.Should().NotBeEmpty().And.NotContain(-1).And.BeInAscendingOrder();
    }

    [Fact]
    public async Task A_finding_is_data_the_interface_words_not_a_sentence()
    {
        var item = await GetAsync("/api/v1/work-items/repository:example/shop");

        var finding = item.GetProperty("findings").EnumerateArray().First();
        finding.GetProperty("kind").GetString().Should().Be("dependencyAlerts");
        finding.GetProperty("alerts").GetProperty("critical").GetInt32().Should().Be(1);
        finding.TryGetProperty("summary", out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_decision_card_has_its_plan_ticked_and_available_but_no_step_granted_unless_the_operator_granted_one()
    {
        using var factory = new ApprovalsDisabledApiFactory();
        var item = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/work-items/repository:example/shop", TestContext.Current.CancellationToken);

        var plan = item.GetProperty("plan").EnumerateArray().ToList();
        plan.Select(s => s.GetProperty("kind").GetString()).Should().Equal("validate", "mergePullRequests", "deploy");
        plan.Should().OnlyContain(s => s.GetProperty("ticked").GetBoolean() && s.GetProperty("available").GetBoolean());
        plan.Should().OnlyContain(s => !s.GetProperty("granted").GetBoolean());
        plan[2].GetProperty("environmentId").GetString().Should().Be("cluster");
        plan[2].GetProperty("environmentName").GetString().Should().Be("Kubernetes cluster");
    }

    [Fact]
    public async Task A_deploy_step_that_cannot_be_done_is_unavailable_unticked_and_gives_its_reason_as_a_code()
    {
        var item = await GetAsync("/api/v1/work-items/repository:example/notes-desktop");

        var deploy = item.GetProperty("plan").EnumerateArray().Single(s => s.GetProperty("kind").GetString() == "deploy");
        deploy.GetProperty("available").GetBoolean().Should().BeFalse();
        deploy.GetProperty("ticked").GetBoolean().Should().BeFalse();
        deploy.GetProperty("reason").GetString().Should().Be("noPlacement");
    }

    [Fact]
    public async Task An_unknown_work_item_is_a_404()
    {
        var response = await _client.GetAsync("/api/v1/work-items/repository:example/nope", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Every_route_of_the_api_only_reads_apart_from_the_two_the_operator_uses_and_the_fallback_that_answers_404_to_everything_else()
    {
        var routes = _factory.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api", StringComparison.Ordinal) == true)
            .ToList();

        routes.Should().NotBeEmpty();
        var withMethods = routes.Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>() is not null).ToList();
        // The only routes that change something: record an approval, and set the global stop. Neither runs anything.
        var writes = withMethods.Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Any(m => m != HttpMethods.Get))
            .Select(e => $"{string.Join(',', e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)} {e.RoutePattern.RawText}");
        writes.Order().Should().Equal("POST /api/v1/approvals", "PUT /api/v1/safety/kill-switch");
        routes.Except(withMethods).Should().ContainSingle().Which.RoutePattern.RawText.Should().Contain("{*path");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Any_write_to_the_api_is_refused_with_a_404(string method)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/api/v1/work-items/repository:example/shop/approve"), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_route_of_the_api_is_a_404()
    {
        var response = await _client.GetAsync("/api/v1/nothing", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_refresh_can_be_asked_for_and_answers_like_a_read()
    {
        var report = await GetAsync("/api/v1/work-items?refresh=true");

        report.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task The_status_names_the_source_and_its_last_read()
    {
        var status = await GetAsync("/api/v1/system/status");

        status.GetProperty("name").GetString().Should().Be("Orchestrarr");
        var source = status.GetProperty("source");
        source.GetProperty("kind").GetString().Should().Be("sample");
        source.GetProperty("configured").GetBoolean().Should().BeTrue();
        source.GetProperty("readAt").ValueKind.Should().Be(JsonValueKind.String);
        source.GetProperty("stale").GetBoolean().Should().BeFalse();
    }
}
