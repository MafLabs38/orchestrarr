using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Core.Test.Accounts;

public sealed class SignInServiceTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_right_password_starts_a_session_that_belongs_to_the_account_and_is_traced()
    {
        var harness = new AccountsHarness();
        var account = harness.Create();

        var signedIn = await harness.SignIn.SignInAsync(" alice ", AccountsHarness.Password, Ct);

        signedIn.Should().NotBeNull();
        harness.Sessions.AccountOf(signedIn.SessionId).Should().Be(account.Id);
        harness.Audit.Events.Should().Contain((account.Subject, AccountEvents.LoginSucceeded, "alice", null));
    }

    [Fact]
    public async Task A_wrong_password_an_unknown_name_and_a_disabled_account_all_get_the_same_nothing()
    {
        var harness = new AccountsHarness();
        harness.Create("alice");
        var disabled = harness.Create("bob", Roles.Reader);
        harness.Service.Update(disabled.Id, Roles.Reader, false, "system");

        (await harness.SignIn.SignInAsync("alice", "not the password", Ct)).Should().BeNull();
        (await harness.SignIn.SignInAsync("nobody", AccountsHarness.Password, Ct)).Should().BeNull();
        (await harness.SignIn.SignInAsync("bob", AccountsHarness.Password, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Only_the_failures_of_a_name_that_exists_are_traced()
    {
        var harness = new AccountsHarness();
        harness.Create("alice");
        harness.Audit.Events.Clear();

        await harness.SignIn.SignInAsync("nobody", "whatever", Ct);
        await harness.SignIn.SignInAsync("alice", "whatever", Ct);

        harness.Audit.Events.Should().ContainSingle().Which.Action.Should().Be(AccountEvents.LoginFailed);
    }

    [Fact]
    public async Task Five_failures_lock_the_name_even_against_the_right_password_until_they_age_out()
    {
        var harness = new AccountsHarness();
        harness.Create("alice");
        for (var attempt = 0; attempt < LoginThrottle.MaxFailures; attempt++)
        {
            await harness.SignIn.SignInAsync("alice", "wrong", Ct);
        }

        var locked = async () => await harness.SignIn.SignInAsync("alice", AccountsHarness.Password, Ct);

        (await locked.Should().ThrowAsync<AccountRejectedException>()).Which.Problem.Should().Be(AccountProblem.Locked);
        harness.Time.Advance(LoginThrottle.Window + TimeSpan.FromSeconds(1));
        (await harness.SignIn.SignInAsync("alice", AccountsHarness.Password, Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_name_that_cannot_be_a_name_is_refused_without_being_counted()
    {
        var harness = new AccountsHarness();

        (await harness.SignIn.SignInAsync("password-check:alice", "x", Ct)).Should().BeNull();
        (await harness.SignIn.SignInAsync("alice", new string('x', PasswordPolicy.MaxLength + 1), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Checking_the_current_password_counts_like_a_sign_in_under_its_own_budget()
    {
        var harness = new AccountsHarness();
        var account = harness.Create();

        (await harness.SignIn.VerifyPasswordAsync(account.Id, AccountsHarness.Password, Ct)).Should().BeTrue();
        for (var attempt = 0; attempt < LoginThrottle.MaxFailures; attempt++)
        {
            (await harness.SignIn.VerifyPasswordAsync(account.Id, "wrong", Ct)).Should().BeFalse();
        }

        var locked = async () => await harness.SignIn.VerifyPasswordAsync(account.Id, AccountsHarness.Password, Ct);
        await locked.Should().ThrowAsync<AccountRejectedException>();
        (await harness.SignIn.SignInAsync("alice", AccountsHarness.Password, Ct)).Should().NotBeNull("guessing the sign-in budget is not the same as the password check budget");
    }

    [Fact]
    public async Task A_session_ends_at_its_absolute_lifetime_and_when_the_person_signs_out()
    {
        var harness = new AccountsHarness(sessionDays: 2);
        harness.Create();
        var first = (await harness.SignIn.SignInAsync("alice", AccountsHarness.Password, Ct))!;
        var second = (await harness.SignIn.SignInAsync("alice", AccountsHarness.Password, Ct))!;

        harness.SignIn.EndSession(second.SessionId);
        harness.Sessions.AccountOf(second.SessionId).Should().BeNull();
        harness.Time.Advance(TimeSpan.FromDays(2) + TimeSpan.FromSeconds(1));

        harness.Sessions.AccountOf(first.SessionId).Should().BeNull();
    }
}
