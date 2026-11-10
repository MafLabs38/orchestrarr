using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Core.Test.Accounts;

/// <summary>What the accounts tests share: the in-memory stores, a recording audit, a clock they move, and the services wired as the host wires them.</summary>
internal sealed class AccountsHarness
{
    public const string Password = "correct horse battery";

    public AccountsHarness(int sessionDays = 30)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero));
        Accounts = new InMemoryAccountStore();
        SessionStore = new InMemorySessionStore();
        Audit = new RecordingAudit();
        Throttle = new LoginThrottle(Time);
        Sessions = new SessionService(SessionStore, Time, TimeSpan.FromDays(sessionDays));
        Hasher = new PasswordHasher<Account>();
        Service = new AccountService(Accounts, Audit, Throttle, Sessions, Hasher, Time);
        SignIn = new SignInService(Accounts, Audit, Throttle, Sessions, new AuditSampler(Time), Hasher);
    }

    public FakeTimeProvider Time { get; }

    public InMemoryAccountStore Accounts { get; }

    public InMemorySessionStore SessionStore { get; }

    public RecordingAudit Audit { get; }

    public LoginThrottle Throttle { get; }

    public SessionService Sessions { get; }

    public PasswordHasher<Account> Hasher { get; }

    public AccountService Service { get; }

    public SignInService SignIn { get; }

    public Account Create(string name = "alice", string role = Roles.Operator) => Service.Create(name, role, Password, "system");

    internal sealed class RecordingAudit : IAccountAudit
    {
        public List<(string Actor, string Action, string Target, string? Detail)> Events { get; } = [];

        public void Record(string actor, string action, string target, string? detail = null) => Events.Add((actor, action, target, detail));
    }
}
