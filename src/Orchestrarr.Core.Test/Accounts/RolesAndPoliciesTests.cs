using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Core.Test.Accounts;

public sealed class RolesAndPoliciesTests
{
    [Fact]
    public void The_administrator_holds_every_permission_so_that_a_new_one_cannot_be_forgotten_for_it()
    {
        Roles.Find(Roles.Admin)!.Permissions.Should().BeEquivalentTo(Permissions.All);
    }

    [Fact]
    public void A_reader_changes_nothing_and_an_operator_does_not_manage_accounts()
    {
        Roles.Find(Roles.Reader)!.Permissions.Should().BeEquivalentTo(Permissions.FleetRead, Permissions.AuditRead);
        var operatorPermissions = Roles.Find(Roles.Operator)!.Permissions;
        operatorPermissions.Should().Contain([Permissions.ApprovalsCreate, Permissions.StopControl]).And.NotContain(Permissions.AccountsManage);
    }

    [Fact]
    public void A_role_that_does_not_exist_gives_nothing()
    {
        Roles.Find("Root").Should().BeNull();
        new Account(Guid.NewGuid(), "ghost", "Root", true, DateTimeOffset.UnixEpoch).Permissions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ab", true)]
    [InlineData("alice.b-c_d", true)]
    [InlineData("a", false)]
    [InlineData(".alice", false)]
    [InlineData("alice bob", false)]
    [InlineData("password-check:alice", false)]
    [InlineData("system", false)]
    [InlineData("Unverified", false)]
    public void A_name_is_short_plain_and_never_one_the_trail_keeps_for_itself(string name, bool valid)
    {
        AccountNames.IsValid(name).Should().Be(valid);
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("a-long-enough-password", true)]
    public void A_password_is_long_enough(string password, bool fine)
    {
        (PasswordPolicy.Problem(password, "alice") is null).Should().Be(fine);
    }

    [Fact]
    public void A_password_is_neither_the_name_nor_absurdly_long()
    {
        PasswordPolicy.Problem("Alice-Alice-Alice", "alice-alice-alice").Should().NotBeNull();
        PasswordPolicy.Problem(new string('x', PasswordPolicy.MaxLength + 1), "alice").Should().NotBeNull();
    }
}
