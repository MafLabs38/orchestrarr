using System.Text;
using System.Text.RegularExpressions;
using Net.Codecrete.QrCodeGenerator;
using Orchestrarr.Core.SecondFactor;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor;

/// <summary>What the commands need from a terminal. A secret read is not echoed.</summary>
public interface IConsoleIo
{
    void WriteLine(string text);

    string ReadSecret(string prompt);
}

/// <summary>
/// The commands that the operator runs on the executor's machine, in their own terminal: enrolling the app that gives their codes, and the standing
/// delegations. The secret of an operator is made here and stays here; it is shown once, on this terminal, and never goes to the control plane.
/// </summary>
public sealed partial class OperatorCommands(ExecutorStore store, SecondFactorGuard guard, TotpOptions totp, string issuer, TimeProvider time, IConsoleIo io)
{
    public async Task<int> EnrolAsync(string subject, bool replace, CancellationToken cancellationToken)
    {
        if (!IsSubject(subject))
        {
            io.WriteLine("The subject must be the identity the control plane signs the operator in with (letters, digits and . _ - @ : +).");
            return 2;
        }
        var enrolled = await store.FindAsync(subject, cancellationToken) is not null;
        if (!replace && enrolled)
        {
            io.WriteLine($"{subject} is already enrolled. Use --replace to give them a new secret (their old app stops working).");
            return 1;
        }

        var secret = OtpAuthUri.NewSecret();
        var uri = OtpAuthUri.Build(issuer, subject, secret, totp);
        io.WriteLine("Scan this with your authenticator app (the code is drawn for a dark terminal; the key below does the same by hand).");
        io.WriteLine("Do not copy this screen into a chat, a note or a ticket: it holds the secret.");
        io.WriteLine(string.Empty);
        io.WriteLine(Qr(uri));
        io.WriteLine($"Key: {Base32.Encode(secret)}");
        io.WriteLine(string.Empty);

        var code = io.ReadSecret("Code shown by the app (to confirm): ").Replace(" ", string.Empty, StringComparison.Ordinal);
        if (code.Length != totp.Digits || Totp.MatchingStep(secret, code, time.GetUtcNow(), totp) is not { } step)
        {
            io.WriteLine("That code is not right: nothing was enrolled.");
            return 1;
        }
        // The code that confirmed the secret is spent: it will not open anything
        store.Enrol(subject, secret, step, time.GetUtcNow());
        store.RecordAct(enrolled ? "secret-replaced" : "enrolled", subject, null, time.GetUtcNow());
        io.WriteLine($"{subject} is enrolled.");
        return 0;
    }

    public async Task<int> AddDelegationAsync(string subject, TaskKind kind, string repository, int days, int quota, CancellationToken cancellationToken)
    {
        if (days is < 1 or > 90 || quota is < 1 or > 1000 || !DelegationScope().IsMatch(repository))
        {
            io.WriteLine("A delegation lasts 1 to 90 days, allows 1 to 1000 tasks a day, and names one repository (example/app) or every repository of one owner (example/*), never more.");
            return 2;
        }
        // Which kinds of task may run without a code is not decided (ADR 0017, "Not decided here"): until it is, nothing that changes a
        // repository can be delegated, and a rule has to name an owner, never every repository.
        if (kind == TaskKind.MergePullRequest)
        {
            io.WriteLine("A merge cannot be delegated yet: which kinds may run without a code is still to be decided (ADR 0017).");
            return 2;
        }
        if (!await ConfirmAsync(subject, cancellationToken))
        {
            return 1;
        }
        var now = time.GetUtcNow();
        var delegation = new Delegation(Guid.NewGuid(), subject, kind, repository, now.AddDays(days), quota, now, null);
        store.AddDelegation(delegation);
        store.RecordAct("delegation-added", subject, $"{delegation.Id:D} {kind} {repository} until {delegation.ExpiresAt:yyyy-MM-dd}, {quota}/day", now);
        io.WriteLine($"Delegation {delegation.Id:D}: {kind} on {repository} until {delegation.ExpiresAt:yyyy-MM-dd}, {quota} tasks a day.");
        return 0;
    }

    public async Task<int> RevokeDelegationAsync(string subject, Guid id, CancellationToken cancellationToken)
    {
        if (!await ConfirmAsync(subject, cancellationToken))
        {
            return 1;
        }
        var revoked = store.RevokeDelegation(id, time.GetUtcNow());
        if (revoked)
        {
            // The subject is who confirmed with their code, which is not always the owner of the delegation
            store.RecordAct("delegation-revoked", subject, $"{id:D}", time.GetUtcNow());
        }
        io.WriteLine(revoked ? $"Delegation {id:D} is revoked." : "No such delegation in force.");
        return 0;
    }

    public int ListActs()
    {
        var acts = store.Acts(50);
        if (acts.Count == 0)
        {
            io.WriteLine("Nothing was done yet.");
            return 0;
        }
        foreach (var (at, act, subject, detail) in acts)
        {
            io.WriteLine($"{at:yyyy-MM-dd HH:mm:ss}  {act,-18}  {subject}{(detail is null ? string.Empty : "  " + detail)}");
        }
        return 0;
    }

    public int ListDelegations()
    {
        var delegations = store.Delegations(includeEnded: true);
        if (delegations.Count == 0)
        {
            io.WriteLine("No delegation.");
            return 0;
        }
        foreach (var delegation in delegations)
        {
            io.WriteLine($"{delegation.Id:D}  {StateOf(delegation),-8}  {delegation.Subject}  {delegation.Kind}  {delegation.Repository}  until {delegation.ExpiresAt:yyyy-MM-dd}  {delegation.DailyQuota}/day");
        }
        return 0;
    }

    public int ListOperators()
    {
        var operators = store.Operators();
        io.WriteLine(operators.Count == 0 ? "No operator is enrolled." : string.Join('\n', operators.Select(entry => $"{entry.Subject}  enrolled {entry.EnrolledAt:yyyy-MM-dd}")));
        return 0;
    }

    private string StateOf(Delegation delegation)
    {
        if (delegation.RevokedAt is not null)
        {
            return "revoked";
        }
        return delegation.ExpiresAt <= time.GetUtcNow() ? "expired" : "in force";
    }

    private async Task<bool> ConfirmAsync(string subject, CancellationToken cancellationToken)
    {
        var code = io.ReadSecret($"Code of {subject}: ");
        var result = await guard.VerifyAsync(subject, code, cancellationToken);
        if (result == SecondFactorResult.Verified)
        {
            return true;
        }
        io.WriteLine($"Refused: {result}.");
        return false;
    }

    // One repository, or every repository of one owner: a delegation never covers more than that
    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,100}/([A-Za-z0-9_.-]{1,100}|\*)\z", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex DelegationScope();

    private static bool IsSubject(string subject) =>
        OrderValidation.IsValidSubject(subject);

    /// <summary>The QR code as text: two rows of modules per line, light modules as blocks, so that it reads on a dark terminal.</summary>
    internal static string Qr(string text)
    {
        var code = QrCode.EncodeText(text, QrCode.Ecc.Medium);
        const int border = 2;
        var builder = new StringBuilder();
        for (var y = -border; y < code.Size + border; y += 2)
        {
            for (var x = -border; x < code.Size + border; x++)
            {
                var top = IsLight(code, x, y);
                var bottom = IsLight(code, x, y + 1);
                builder.Append((top, bottom) switch
                {
                    (true, true) => '█',
                    (true, false) => '▀',
                    (false, true) => '▄',
                    _ => ' ',
                });
            }
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static bool IsLight(QrCode code, int x, int y) => !(x >= 0 && y >= 0 && x < code.Size && y < code.Size && code.GetModule(x, y));
}
