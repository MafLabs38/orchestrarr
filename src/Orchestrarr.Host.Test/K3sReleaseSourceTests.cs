using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Infrastructure;
using Orchestrarr.Host.Decisions;

namespace Orchestrarr.Host.Test;

public sealed class K3sReleaseSourceTests
{
    private const string Channels = """{ "data": [ { "id": "stable", "latest": "v1.37.2+k3s1" }, { "id": "latest", "latest": "v1.37.2+k3s1" }, { "id": "testing", "latest": "v1.18.2-rc3+k3s1" }, { "id": "v1.36", "latest": "v1.36.6+k3s1" }, { "id": "v1.37", "latest": "v1.37.2+k3s1" } ] }""";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void The_stable_channel_and_the_newest_of_each_minor_are_read_and_the_other_channels_are_not()
    {
        var releases = K3sReleaseSource.Parse(Channels)!;

        releases.Stable.ToString().Should().Be("v1.37.2+k3s1");
        releases.LatestByMinor.Keys.Should().BeEquivalentTo([36, 37]);
        K3sReleaseSource.Parse("""{ "data": [ { "id": "v1.36", "latest": "v1.36.6+k3s1" } ] }""").Should().BeNull("without a stable channel nothing is known");
    }

    [Fact]
    public async Task What_was_read_is_kept_for_hours_a_failure_keeps_the_last_good_answer_and_an_empty_address_reads_nothing()
    {
        var handler = new Answering(Channels);
        using var source = new K3sReleaseSource(new PlatformOptions { K3sChannelsUrl = "https://channels.example/", CacheMinutes = 60 }, _time, handler);

        var first = await source.GetAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(30));
        await source.GetAsync(TestContext.Current.CancellationToken);
        handler.Calls.Should().Be(1);

        _time.Advance(TimeSpan.FromMinutes(40));
        handler.Fail = true;
        var kept = await source.GetAsync(TestContext.Current.CancellationToken);

        kept.Should().BeSameAs(first);
        source.Peek().Should().BeSameAs(first);
        using var off = new K3sReleaseSource(new PlatformOptions { K3sChannelsUrl = "" }, _time);
        (await off.GetAsync(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public void An_address_that_is_not_https_stops_the_start() =>
        new Func<K3sReleaseSource>(() => new K3sReleaseSource(new PlatformOptions { K3sChannelsUrl = "http://channels.example/" }, _time)).Should().Throw<InvalidOperationException>().WithMessage("*https*");

    private sealed class Answering(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.BadGateway : HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
