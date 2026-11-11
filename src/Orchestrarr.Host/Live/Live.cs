using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;

namespace Orchestrarr.Host.Live;

/// <summary>
/// The channel that tells the screens that something changed (ADR 0024): a WebSocket (SignalR, with the fallbacks it has). It carries no data of the fleet, of
/// a person or of a run, only <c>(topic, id)</c>: the screen then reads what changed through the API, which asks for the session like any other route.
/// A client that is not allowed to read the fleet cannot connect; one that stayed connected after signing out receives pings it cannot do anything with.
/// </summary>
public sealed class LiveHub : Hub
{
    public const string Path = "/api/v1/live";

    public const string Changed = "changed";
}

/// <summary>What the host says changed. The topics are the ones the screens know: <c>runs</c> (with the id of the run), <c>approvals</c>, <c>audit</c>, <c>status</c>, <c>problems</c>.</summary>
public static class LiveTopics
{
    public const string Runs = "runs";
    public const string Approvals = "approvals";
    public const string Audit = "audit";
    public const string Status = "status";
    public const string Problems = "problems";
    public const string Accounts = "accounts";
    public const string Options = "options";
}

public interface ILiveNotifier
{
    /// <summary>Says that something changed. Never throws and never waits: a screen that cannot be told must not hold up what changed.</summary>
    void Publish(string topic, string? id = null);
}

/// <summary>
/// Collects what changed and sends each distinct <c>(topic, id)</c> once per interval, so that a burst (a run that writes many lines, a refresh that
/// reads thirty repositories) is a few messages. The last change of a burst is always sent: it waits for the next interval, it is not dropped.
/// </summary>
public sealed class LiveBroadcaster(IHubContext<LiveHub> hub, ILogger<LiveBroadcaster> logger, TimeProvider time) : BackgroundService, ILiveNotifier
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<(string Topic, string? Id), byte> _changed = new();

    public void Publish(string topic, string? id = null) => _changed[(topic, id)] = 0;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await FlushAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down
        }
    }

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        foreach (var key in _changed.Keys.ToList())
        {
            _changed.TryRemove(key, out _);
            try
            {
                await hub.Clients.All.SendAsync(LiveHub.Changed, key.Topic, key.Id, cancellationToken);
            }
            catch (Exception failure) when (failure is InvalidOperationException or OperationCanceledException or IOException)
            {
                // A client that went away is not the host's problem; the next change is sent as usual
                logger.LogDebug(failure, "A change could not be sent to the screens");
            }
        }
    }
}
