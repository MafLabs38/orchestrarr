using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Orchestrarr.Host.Test;

public sealed class WorkItemsApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public WorkItemsApiTests(WebApplicationFactory<Program> factory)
    {
        var sample = Path.Combine(AppContext.BaseDirectory, "portarr-maintenance.sample.json");
        _client = factory.WithWebHostBuilder(builder => builder.UseSetting("Portarr:SampleFile", sample)).CreateClient();
    }

    [Fact]
    public async Task The_work_items_come_most_severe_first_with_camel_case_names_and_enums_as_text()
    {
        var report = await _client.GetFromJsonAsync<JsonElement>("/api/v1/work-items", TestContext.Current.CancellationToken);

        report.GetProperty("sourceKind").GetString().Should().Be("sample");
        report.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null);
        var items = report.GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(5);
        items[0].GetProperty("severity").GetString().Should().Be("danger");
        items[0].GetProperty("id").GetString().Should().Be("repository:example/notes-desktop");
        items[^1].GetProperty("severity").GetString().Should().Be("info");
    }

    [Fact]
    public async Task A_decision_card_has_its_plan_with_every_available_step_ticked()
    {
        var item = await _client.GetFromJsonAsync<JsonElement>("/api/v1/work-items/repository:example/shop", TestContext.Current.CancellationToken);

        var plan = item.GetProperty("plan").EnumerateArray().ToList();
        plan.Select(s => s.GetProperty("kind").GetString()).Should().Equal("validate", "merge", "deploy");
        plan.Should().OnlyContain(s => s.GetProperty("ticked").GetBoolean() && s.GetProperty("available").GetBoolean());
        plan[2].GetProperty("environmentId").GetString().Should().Be("cluster");
    }

    [Fact]
    public async Task An_unknown_work_item_is_a_404()
    {
        var response = await _client.GetAsync("/api/v1/work-items/repository:example/nope", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task There_is_no_route_to_approve_anything_yet()
    {
        var response = await _client.PostAsync("/api/v1/work-items/repository:example/shop/approve", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task The_status_names_the_source_and_its_last_read()
    {
        var status = await _client.GetFromJsonAsync<JsonElement>("/api/v1/system/status", TestContext.Current.CancellationToken);

        status.GetProperty("name").GetString().Should().Be("Orchestrarr");
        var source = status.GetProperty("source");
        source.GetProperty("kind").GetString().Should().Be("sample");
        source.GetProperty("configured").GetBoolean().Should().BeTrue();
        source.GetProperty("stale").GetBoolean().Should().BeFalse();
    }
}
