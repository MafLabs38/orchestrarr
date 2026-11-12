using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Net;
using Microsoft.AspNetCore.SignalR;
using Orchestrarr.Host.Live;

namespace Orchestrarr.Host.Test;

/// <summary>The channel that tells the screens what changed (ADR 0025): it says what to read again, only to people who may read the fleet.</summary>
public sealed class LiveTests : IClassFixture<SampleApiFactory>, IClassFixture<AccountsApiFactory>
{
    private const string Header = "X-Orchestrarr-Request";

    private readonly SampleApiFactory _signedIn;
    private readonly AccountsApiFactory _real;

    public LiveTests(SampleApiFactory signedIn, AccountsApiFactory real)
    {
        _signedIn = signedIn;
        _real = real;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (LiveBroadcaster Broadcaster, IClientProxy Clients) Broadcaster()
    {
        var clients = Substitute.For<IClientProxy>();
        var hub = Substitute.For<IHubContext<LiveHub>>();
        hub.Clients.All.Returns(clients);
        return (new LiveBroadcaster(hub, NullLogger<LiveBroadcaster>.Instance, TimeProvider.System), clients);
    }

    [Fact]
    public async Task A_burst_of_the_same_change_is_one_message_and_each_distinct_change_is_sent()
    {
        var (broadcaster, clients) = Broadcaster();
        for (var index = 0; index < 3; index++)
        {
            broadcaster.Publish(LiveTopics.Runs, "run-1");
        }
        broadcaster.Publish(LiveTopics.Runs, "run-2");
        broadcaster.Publish(LiveTopics.Audit);

        await broadcaster.FlushAsync(Ct);

        await clients.Received(1).SendCoreAsync(LiveHub.Changed, Arg.Is<object?[]>(arguments => (string)arguments[0]! == LiveTopics.Runs && (string?)arguments[1] == "run-1"), Ct);
        await clients.Received(1).SendCoreAsync(LiveHub.Changed, Arg.Is<object?[]>(arguments => (string)arguments[0]! == LiveTopics.Runs && (string?)arguments[1] == "run-2"), Ct);
        await clients.Received(1).SendCoreAsync(LiveHub.Changed, Arg.Is<object?[]>(arguments => (string)arguments[0]! == LiveTopics.Audit && arguments[1] == null), Ct);
        await clients.Received(3).SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_change_that_was_sent_is_not_sent_again_and_a_client_that_went_away_does_not_stop_the_others()
    {
        var (broadcaster, clients) = Broadcaster();
        clients.SendCoreAsync(LiveHub.Changed, Arg.Is<object?[]>(arguments => (string)arguments[0]! == LiveTopics.Status), Arg.Any<CancellationToken>()).Returns(_ => throw new InvalidOperationException("gone"));
        broadcaster.Publish(LiveTopics.Status);
        broadcaster.Publish(LiveTopics.Approvals);

        await broadcaster.FlushAsync(Ct);
        await broadcaster.FlushAsync(Ct);

        await clients.Received(1).SendCoreAsync(LiveHub.Changed, Arg.Is<object?[]>(arguments => (string)arguments[0]! == LiveTopics.Approvals), Arg.Any<CancellationToken>());
        await clients.Received(2).SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_person_who_may_read_the_fleet_can_open_the_channel()
    {
        var client = _signedIn.CreateClient();
        client.DefaultRequestHeaders.Add(Header, "test");

        var response = await client.PostAsync($"{LiveHub.Path}/negotiate?negotiateVersion=1", content: null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("WebSockets");
    }

    [Fact]
    public async Task Nobody_who_has_not_signed_in_can_open_the_channel()
    {
        var client = _real.CreateClient();
        client.DefaultRequestHeaders.Add(Header, "test");

        var response = await client.PostAsync($"{LiveHub.Path}/negotiate?negotiateVersion=1", content: null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
