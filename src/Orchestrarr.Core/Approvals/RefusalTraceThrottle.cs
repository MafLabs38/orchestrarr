namespace Orchestrarr.Core.Approvals;

/// <summary>
/// The same person refused for the same reason is traced once in a while, not at every try: a loop of refusals must not use up the share of the
/// trail that is kept for them. What matters (who, what for, that it went on) is in the first entry. One claim at a time under one lock, so that
/// identical refusals at once are traced once; a claim whose trace could not be written is given back, so that the next one is traced. It is
/// bounded: when it remembers too much and nothing is old enough to forget, a refusal is traced without being remembered, never dropped.
/// </summary>
public sealed class RefusalTraceThrottle(TimeProvider time, TimeSpan? window = null, int memory = RefusalTraceThrottle.DefaultMemory)
{
    public const int DefaultMemory = 1024;

    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _window = window ?? DefaultWindow;
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Subject, ApprovalRefusal Refusal), DateTimeOffset> _claimed = [];

    /// <summary>Whether this refusal is to be traced now: false when the same one was traced within the window.</summary>
    public bool TryClaim(string subject, ApprovalRefusal refusal)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (_claimed.TryGetValue((subject, refusal), out var last) && now - last < _window)
            {
                return false;
            }
            if (_claimed.Count >= memory)
            {
                foreach (var key in _claimed.Where(entry => now - entry.Value >= _window).Select(entry => entry.Key).ToList())
                {
                    _claimed.Remove(key);
                }
            }
            if (_claimed.Count < memory)
            {
                _claimed[(subject, refusal)] = now;
            }
            return true;
        }
    }

    /// <summary>The trace could not be written: the next identical refusal is traced.</summary>
    public void Release(string subject, ApprovalRefusal refusal)
    {
        lock (_lock)
        {
            _claimed.Remove((subject, refusal));
        }
    }
}
