using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Accounts;

namespace Orchestrarr.Core.Test.Accounts;

public sealed class AccountSeederTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("orchestrarr-seeder").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string PasswordFile(string password)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, password + "\n");
        return path;
    }

    private static AccountSeeder Seeder(AccountsHarness harness, AuthSettings settings) =>
        new(harness.Accounts, harness.Service, settings, NullLogger<AccountSeeder>.Instance);

    private static AuthSettings Settings(string? user = null, string? file = null, string? resetUser = null, string? resetFile = null) =>
        new(user, file, resetUser, resetFile, TimeSpan.FromHours(12), TimeSpan.FromDays(30));

    [Fact]
    public void With_no_account_the_first_administrator_is_created_from_the_file_and_never_again()
    {
        var harness = new AccountsHarness();
        var settings = Settings("root", PasswordFile("a very long first password"));

        Seeder(harness, settings).Run();
        harness.Service.SetPassword(harness.Accounts.GetCredentials("root")!.Account.Id, "the password changed since", "root");
        Seeder(harness, settings).Run();

        var root = harness.Accounts.GetCredentials("root")!.Account;
        root.Role.Should().Be(Roles.Admin);
        harness.Accounts.Count().Should().Be(1);
        harness.Hasher.VerifyHashedPassword(root, harness.Accounts.GetCredentials("root")!.PasswordHash!, "the password changed since")
            .Should().NotBe(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed, "a second start does not reset what a person changed");
    }

    [Fact]
    public void With_no_setting_nobody_is_created_and_nothing_stops()
    {
        var harness = new AccountsHarness();

        Seeder(harness, Settings()).Run();

        harness.Accounts.Count().Should().Be(0);
    }

    [Fact]
    public void A_password_file_that_cannot_be_read_or_a_password_that_is_refused_stops_the_start_with_a_message_that_says_which()
    {
        var harness = new AccountsHarness();

        var missing = () => Seeder(harness, Settings("root", Path.Combine(_folder, "absent"))).Run();
        var weak = () => Seeder(harness, Settings("root", PasswordFile("short"))).Run();

        missing.Should().Throw<InvalidOperationException>().WithMessage("*cannot be read*");
        weak.Should().Throw<InvalidOperationException>().WithMessage("*first administrator was refused*");
    }

    [Fact]
    public void The_break_glass_setting_resets_the_password_of_the_named_account_at_each_start()
    {
        var harness = new AccountsHarness();
        var account = harness.Create("root", Roles.Admin);
        harness.Create("second", Roles.Admin);
        harness.Service.Update(account.Id, Roles.Admin, false, "second");

        Seeder(harness, Settings(resetUser: "root", resetFile: PasswordFile("the break glass password"))).Run();

        harness.Accounts.Get(account.Id)!.Enabled.Should().BeTrue();
        harness.Hasher.VerifyHashedPassword(account, harness.Accounts.GetCredentials("root")!.PasswordHash!, "the break glass password")
            .Should().NotBe(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed);
    }
}
