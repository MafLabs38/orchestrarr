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

    [Theory]
    [InlineData("[]")]
    [InlineData("{ \"data\": \"nothing\" }")]
    [InlineData("{ \"data\": [ 1, \"x\", { \"id\": 3, \"latest\": 4 } ] }")]
    [InlineData("{ \"data\": [ { \"id\": \"stable\", \"latest\": \"v1.99999999999.0+k3s1\" } ] }")]
    public void A_body_that_is_not_what_the_channels_document_gives_nothing_and_never_throws(string body) => K3sReleaseSource.Parse(body).Should().BeNull();

    [Fact]
    public async Task An_answer_that_says_nothing_usable_keeps_the_last_good_releases()
    {
        var handler = new Answering(Channels);
        using var source = new K3sReleaseSource(new PlatformOptions { K3sChannelsUrl = "https://channels.example/", CacheMinutes = 60 }, _time, handler);
        var good = await source.GetAsync(TestContext.Current.CancellationToken);

        _time.Advance(TimeSpan.FromMinutes(70));
        handler.Body = "[]";
        var kept = await source.GetAsync(TestContext.Current.CancellationToken);

        good.Should().NotBeNull();
        kept.Should().BeSameAs(good);
    }

    [Fact]
    public async Task A_slow_reading_does_not_hold_the_request_and_the_screens_are_told_when_it_ends()
    {
        var handler = new Answering(Channels) { Gate = new TaskCompletionSource() };
        using var source = new K3sReleaseSource(new PlatformOptions { K3sChannelsUrl = "https://channels.example/", FirstWaitSeconds = 5 }, _time, handler);
        var changed = new TaskCompletionSource();
        source.Changed += () => changed.TrySetResult();

        var request = source.GetAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(5));
        var first = await request;

        first.Should().BeNull("nothing was read yet and the request did not wait for it");
        handler.Gate.SetResult();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        source.Peek().Should().NotBeNull();
    }

    [Fact]
    public void An_address_that_is_not_https_stops_the_start() =>
        new Func<K3sReleaseSource>(() => new K3sReleaseSource(new PlatformOptions { K3sChannelsUrl = "http://channels.example/" }, _time)).Should().Throw<InvalidOperationException>().WithMessage("*https*");

    private sealed class Answering(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public bool Fail { get; set; }

        public string Body { get; set; } = body;

        public TaskCompletionSource? Gate { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Gate is not null)
            {
                await Gate.Task;
            }
            return new HttpResponseMessage(Fail ? HttpStatusCode.BadGateway : HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }
}
