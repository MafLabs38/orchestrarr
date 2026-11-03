using System.Net.Http.Json;
using System.Text.Json;

namespace Orchestrarr.Host.Test;

public sealed class SourceStatusApiTests
{
    [Fact]
    public async Task With_no_source_configured_the_status_says_so_and_there_is_nothing_to_decide()
    {
        using var factory = new UnconfiguredApiFactory();
        var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/system/status", TestContext.Current.CancellationToken);
        var report = await client.GetFromJsonAsync<JsonElement>("/api/v1/work-items", TestContext.Current.CancellationToken);

        status.GetProperty("source").GetProperty("kind").GetString().Should().Be("none");
        status.GetProperty("source").GetProperty("configured").GetBoolean().Should().BeFalse();
        report.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_source_that_is_down_gives_a_code_and_never_the_text_of_the_failure()
    {
        using var factory = new FailingApiFactory();
        var client = factory.CreateClient();

        var statusText = await client.GetStringAsync("/api/v1/system/status", TestContext.Current.CancellationToken);
        var reportText = await client.GetStringAsync("/api/v1/work-items", TestContext.Current.CancellationToken);

        using var status = JsonDocument.Parse(statusText);
        status.RootElement.GetProperty("source").GetProperty("error").GetString().Should().Be("unreachable");
        JsonDocument.Parse(reportText).RootElement.GetProperty("error").GetString().Should().Be("unreachable");
        (statusText + reportText).Should().NotContain("internal-portarr").And.NotContain("8484");
    }
}
