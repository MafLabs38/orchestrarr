using Orchestrarr.Host.Accounts;

namespace Orchestrarr.Host.Test;

/// <summary>A wrong <c>Auth</c> setting stops the host at its start rather than leave a door open (ADR 0018).</summary>
public sealed class AuthOptionsTests
{
    [Fact]
    public void The_defaults_are_valid_and_the_cookie_is_secure()
    {
        var config = new AuthOptions().Validate();

        config.SecureCookie.Should().BeTrue();
        config.Settings.AdminUser.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(AuthOptions.MaxSessionHours + 1, 30)]
    [InlineData(12, 0)]
    [InlineData(12, AuthOptions.MaxSessionDays + 1)]
    public void A_session_lifetime_out_of_bounds_stops_the_host(int hours, int days)
    {
        var options = new AuthOptions { SessionHours = hours, SessionMaxDays = days };

        options.Invoking(candidate => candidate.Validate()).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("root", null)]
    [InlineData(null, "/run/secrets/admin")]
    [InlineData("root", " ")]
    public void The_first_administrator_needs_a_name_and_a_password_file_together(string? user, string? file)
    {
        var options = new AuthOptions { AdminUser = user, AdminPasswordFile = file };

        options.Invoking(candidate => candidate.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*AdminUser*AdminPasswordFile*");
    }

    [Theory]
    [InlineData("root", null)]
    [InlineData(null, "/run/secrets/reset")]
    public void The_break_glass_needs_a_name_and_a_password_file_together(string? user, string? file)
    {
        var options = new AuthOptions { ResetPasswordUser = user, ResetPasswordFile = file };

        options.Invoking(candidate => candidate.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*ResetPassword*");
    }

    [Fact]
    public void A_cookie_setting_that_does_not_exist_stops_the_host()
    {
        var options = new AuthOptions { CookieSecurity = (CookieSecurityMode)99 };

        options.Invoking(candidate => candidate.Validate()).Should().Throw<InvalidOperationException>().WithMessage("*CookieSecurity*");
    }

    [Fact]
    public void A_complete_setting_is_read_and_trimmed()
    {
        var config = new AuthOptions { AdminUser = " root ", AdminPasswordFile = " /run/secrets/admin ", CookieSecurity = CookieSecurityMode.SameAsRequest }.Validate();

        config.Settings.AdminUser.Should().Be("root");
        config.Settings.AdminPasswordFile.Should().Be("/run/secrets/admin");
        config.SecureCookie.Should().BeFalse();
    }
}
