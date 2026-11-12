using System.Collections.Concurrent;

namespace Orchestrarr.Core.SecondFactor;

/// <summary>
/// Where the secrets of the operators are kept. It belongs to the component that checks the code, never to the one that relays it: a
/// control plane that held them could make any code (ADR 0017).
/// </summary>
public interface ISecondFactorSecrets
{
    /// <summary>The secret of this operator, or null when none was enrolled for them.</summary>
    ValueTask<byte[]?> FindAsync(string subject, CancellationToken cancellationToken);
}

/// <summary>Remembers the last step accepted for each operator, so that a code is good once.</summary>
public interface ISecondFactorUseLedger
{
    /// <summary>
    /// True and recorded when <paramref name="step"/> is later than any step already accepted for this operator, false otherwise. It
    /// must be atomic: two requests with the same code never both succeed.
    /// </summary>
    ValueTask<bool> TryAdvanceAsync(string subject, long step, CancellationToken cancellationToken);
}

/// <summary>Why a code was not accepted. A code for the trail, never a sentence for the person (an attacker learns nothing from the difference).</summary>
public enum SecondFactorResult
{
    Verified,
    Malformed,
    UnknownSubject,
    Wrong,
    Reused,
    LockedOut,
}

public sealed record SecondFactorOptions
{
    public TotpOptions Totp { get; init; } = new();

    /// <summary>Wrong codes tolerated for one operator within <see cref="FailureWindow"/> before they are locked out.</summary>
    public int MaxFailures { get; init; } = 5;

    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan LockoutFor { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How many operators the failure counters follow at once; the oldest is forgotten past it, so that made-up names cannot grow the memory.</summary>
    public int TrackedSubjects { get; init; } = 1024;
}

/// <summary>
/// Checks the one-time code of an operator: the shape, the lockout, the secret, the match and the single use, in that order. It fails
/// closed: anything that is not <see cref="SecondFactorResult.Verified"/> refuses. The counters are in memory, which is enough because
/// the process that holds them is the one that checks.
/// </summary>
public sealed class SecondFactorGuard(ISecondFactorSecrets secrets, ISecondFactorUseLedger ledger, TimeProvider time, SecondFactorOptions options)
{
    private readonly ConcurrentDictionary<string, Failures> _failures = new(StringComparer.Ordinal);

    public async ValueTask<SecondFactorResult> VerifyAsync(string subject, string? code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var now = time.GetUtcNow();
        var cleaned = Clean(code);
        if (cleaned is null || cleaned.Length != options.Totp.Digits || !cleaned.All(char.IsAsciiDigit))
        {
            return SecondFactorResult.Malformed;
        }
        if (IsLockedOut(subject, now))
        {
            return SecondFactorResult.LockedOut;
        }

        var secret = await secrets.FindAsync(subject, cancellationToken);
        if (secret is null)
        {
            // Nothing to guess for a subject that is not enrolled, so nothing is counted: invented names must not push a real lockout out of the table
            return SecondFactorResult.UnknownSubject;
        }
        if (Totp.MatchingStep(secret, cleaned, now, options.Totp) is not { } step)
        {
            Fail(subject, now);
            return SecondFactorResult.Wrong;
        }
        if (!await ledger.TryAdvanceAsync(subject, step, cancellationToken))
        {
            return SecondFactorResult.Reused;
        }
        _failures.TryRemove(subject, out _);
        return SecondFactorResult.Verified;
    }

    private static string? Clean(string? code) => code is null ? null : new string([.. code.Where(c => c != ' ')]);

    private bool IsLockedOut(string subject, DateTimeOffset now) =>
        _failures.TryGetValue(subject, out var failures) && failures.LockedUntil is { } until && until > now;

    private void Fail(string subject, DateTimeOffset now)
    {
        if (!_failures.ContainsKey(subject) && _failures.Count >= options.TrackedSubjects)
        {
            // Forget the oldest follow-up: a flood of invented names must not grow the memory without bound
            var oldest = _failures.OrderBy(pair => pair.Value.Last).First().Key;
            _failures.TryRemove(oldest, out _);
        }
        _failures.AddOrUpdate(
            subject,
            _ => new Failures(1, now, options.MaxFailures <= 1 ? now + options.LockoutFor : null),
            (_, current) =>
            {
                // A lockout that ended, or failures older than the window, start the count again
                var fresh = current.LockedUntil is { } until && until <= now || now - current.Last > options.FailureWindow;
                var count = fresh ? 1 : current.Count + 1;
                return new Failures(count, now, count >= options.MaxFailures ? now + options.LockoutFor : null);
            });
    }

    private sealed record Failures(int Count, DateTimeOffset Last, DateTimeOffset? LockedUntil);
}

/// <summary>A ledger in memory, for the tests and for a sandbox that does not keep anything. A real runner keeps it in its own file.</summary>
public sealed class InMemorySecondFactorUseLedger : ISecondFactorUseLedger
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _last = new(StringComparer.Ordinal);

    public ValueTask<bool> TryAdvanceAsync(string subject, long step, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_last.TryGetValue(subject, out var last) && step <= last)
            {
                return ValueTask.FromResult(false);
            }
            _last[subject] = step;
            return ValueTask.FromResult(true);
        }
    }
}
