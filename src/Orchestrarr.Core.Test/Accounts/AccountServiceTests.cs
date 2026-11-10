using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Core.Test.Accounts;

public sealed class AccountServiceTests
{
    [Fact]
    public void Creating_an_account_keeps_the_hash_and_not_the_password_and_traces_who_did_it()
    {
        var harness = new AccountsHarness();

        var account = harness.Create("alice", Roles.Operator);

        harness.Accounts.GetCredentials("ALICE")!.PasswordHash.Should().NotBeNullOrEmpty().And.NotContain(AccountsHarness.Password);
        account.Subject.Should().Be(account.Id.ToString("D"));
        harness.Audit.Events.Should().ContainSingle().Which.Should().Be(("system", AccountEvents.AccountCreated, "alice", "role Operator"));
    }

    [Theory]
    [InlineData("a", Roles.Reader, "a-long-enough-password")]
    [InlineData("alice", "Root", "a-long-enough-password")]
    [InlineData("alice", Roles.Reader, "short")]
    public void A_name_a_role_or_a_password_that_is_not_fine_is_refused_and_nothing_is_stored(string name, string role, string password)
    {
        var harness = new AccountsHarness();

        var create = () => harness.Service.Create(name, role, password, "system");

        create.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Invalid);
        harness.Accounts.Count().Should().Be(0);
    }

    [Fact]
    public void A_name_is_taken_whatever_its_case()
    {
        var harness = new AccountsHarness();
        harness.Create("alice");

        var again = () => harness.Create("ALICE");

        again.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Conflict);
    }

    [Fact]
    public void The_last_enabled_administrator_can_be_neither_demoted_disabled_nor_deleted()
    {
        var harness = new AccountsHarness();
        var admin = harness.Create("root", Roles.Admin);

        var demote = () => harness.Service.Update(admin.Id, Roles.Operator, true, "root");
        var disable = () => harness.Service.Update(admin.Id, Roles.Admin, false, "root");
        var delete = () => harness.Service.Delete(admin.Id, "root");

        demote.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.Conflict);
        disable.Should().Throw<AccountRejectedException>();
        delete.Should().Throw<AccountRejectedException>();
        harness.Create("second", Roles.Admin);
        harness.Service.Update(admin.Id, Roles.Operator, true, "second").Role.Should().Be(Roles.Operator);
    }

    [Fact]
    public void Disabling_an_account_ends_its_sessions_and_enabling_it_again_does_not_bring_them_back()
    {
        var harness = new AccountsHarness();
        var account = harness.Create();
        var session = harness.Sessions.Start(account.Id);

        harness.Service.Update(account.Id, account.Role, false, "system");
        harness.Service.Update(account.Id, account.Role, true, "system");

        harness.Sessions.AccountOf(session).Should().BeNull();
    }

    [Fact]
    public void A_new_password_ends_the_other_sessions_and_keeps_the_one_of_the_person_who_changed_it()
    {
        var harness = new AccountsHarness();
        var account = harness.Create();
        var mine = harness.Sessions.Start(account.Id);
        var stolen = harness.Sessions.Start(account.Id);

        harness.Service.SetPassword(account.Id, "another long password", account.Subject, keepSession: mine);

        harness.Sessions.AccountOf(mine).Should().Be(account.Id);
        harness.Sessions.AccountOf(stolen).Should().BeNull();
        harness.Audit.Events.Should().Contain(entry => entry.Action == AccountEvents.AccountOwnSignInChanged);
    }

    [Fact]
    public void A_reset_sets_the_password_enables_the_account_lifts_the_lockout_and_ends_the_sessions()
    {
        var harness = new AccountsHarness();
        var account = harness.Create("root", Roles.Admin);
        harness.Create("second", Roles.Admin);
        harness.Service.Update(account.Id, Roles.Admin, false, "second");
        var session = harness.Sessions.Start(account.Id);
        for (var attempt = 0; attempt < LoginThrottle.MaxFailures; attempt++)
        {
            harness.Throttle.TryReserve("root");
        }

        harness.Service.Reset("root", "a brand new password");

        harness.Accounts.Get(account.Id)!.Enabled.Should().BeTrue();
        harness.Sessions.AccountOf(session).Should().BeNull();
        harness.Throttle.TryReserve("root").Should().BeTrue();
    }

    [Fact]
    public void A_value_left_out_of_an_update_keeps_the_current_one()
    {
        var harness = new AccountsHarness();
        var account = harness.Create("alice", Roles.Operator);
        harness.Service.Update(account.Id, null, false, "root");

        var changed = harness.Service.Update(account.Id, Roles.Reader, null, "root");

        changed.Should().Match<Account>(updated => updated.Role == Roles.Reader && !updated.Enabled);
        harness.Accounts.Get(account.Id).Should().Be(changed);
    }

    [Fact]
    public void Updating_or_deleting_an_account_that_does_not_exist_is_not_found()
    {
        var harness = new AccountsHarness();

        var update = () => harness.Service.Update(Guid.NewGuid(), Roles.Reader, true, "root");
        var delete = () => harness.Service.Delete(Guid.NewGuid(), "root");

        update.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.NotFound);
        delete.Should().Throw<AccountRejectedException>().Which.Problem.Should().Be(AccountProblem.NotFound);
    }
}
