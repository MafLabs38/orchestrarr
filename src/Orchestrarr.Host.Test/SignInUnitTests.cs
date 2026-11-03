using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Orchestrarr.Host.SignIn;

namespace Orchestrarr.Host.Test;

public sealed class SignInUnitTests
{
    private static SignInOptions Options(string? stepUpAcr = null) => new() { OperatorGroup = "orchestrarr-operators", GroupsClaim = "groups", StepUpAcr = stepUpAcr };

    private static ClaimsPrincipal Person(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(claim => new Claim(claim.Type, claim.Value)), authenticationType: "test"));

    private static SignInOptions Valid() => new()
    {
        Authority = "https://sso.example/realms/example",
        ClientId = "orchestrarr",
        ClientSecret = "not-a-real-secret",
        PublicUrl = "https://orchestrarr.example",
    };

    // --- who is an operator

    [Fact]
    public void Nobody_signed_in_is_nobody()
    {
        ApproverClaims.From(new ClaimsPrincipal(new ClaimsIdentity()), Options()).Should().BeNull();
    }

    [Theory]
    [InlineData("orchestrarr-operators")]
    [InlineData("/orchestrarr-operators")]
    public void A_member_of_the_operator_group_is_an_operator_whether_or_not_the_group_is_written_with_its_path(string group)
    {
        var approver = ApproverClaims.From(Person(("sub", "alice-1"), ("groups", group)), Options());

        approver.Should().NotBeNull();
        approver.Subject.Should().Be("alice-1");
        approver.IsOperator.Should().BeTrue();
        approver.IsPerson.Should().BeTrue();
    }

    [Fact]
    public void Groups_sent_as_one_json_array_count_too()
    {
        var claim = new Claim("groups", "[\"staff\",\"orchestrarr-operators\"]", "JSON_ARRAY");
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice-1"), claim], "test"));

        ApproverClaims.From(user, Options())!.IsOperator.Should().BeTrue();
    }

    [Fact]
    public void A_broken_json_array_makes_nobody_an_operator()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice-1"), new Claim("groups", "[not json", "JSON_ARRAY")], "test"));

        ApproverClaims.From(user, Options())!.IsOperator.Should().BeFalse();
    }

    [Theory]
    [InlineData("staff")]
    [InlineData("orchestrarr-operators-extra")]
    [InlineData("Orchestrarr-Operators")]
    [InlineData("a/orchestrarr-operators")]
    public void Another_group_is_not_the_operator_group(string group)
    {
        ApproverClaims.From(Person(("sub", "alice-1"), ("groups", group)), Options())!.IsOperator.Should().BeFalse();
    }

    [Fact]
    public void The_claim_that_lists_the_groups_is_the_configured_one()
    {
        var options = new SignInOptions { OperatorGroup = "ops", GroupsClaim = "roles" };

        ApproverClaims.From(Person(("sub", "a"), ("roles", "ops")), options)!.IsOperator.Should().BeTrue();
        ApproverClaims.From(Person(("sub", "a"), ("groups", "ops")), options)!.IsOperator.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("alice 1")]
    [InlineData("alice;1")]
    public void An_identity_without_a_usable_subject_is_nobody(string? subject)
    {
        var claims = subject is null ? [("groups", "orchestrarr-operators")] : new[] { ("sub", subject), ("groups", "orchestrarr-operators") };

        ApproverClaims.From(Person(claims), Options()).Should().BeNull();
    }

    [Fact]
    public void The_name_and_the_group_are_not_a_subject()
    {
        ApproverClaims.From(Person(("preferred_username", "alice"), ("groups", "orchestrarr-operators")), Options()).Should().BeNull();
    }

    // --- the second factor

    [Fact]
    public void A_session_at_the_step_up_level_carries_the_moment_of_the_second_factor()
    {
        var approver = ApproverClaims.From(Person(("sub", "a"), ("acr", "mfa"), ("auth_time", "1793700000")), Options("mfa"));

        approver!.StepUpAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1793700000));
    }

    [Theory]
    [InlineData("password", "1793700000")]
    [InlineData("mfa", "")]
    [InlineData("mfa", "yesterday")]
    [InlineData("mfa", "-5")]
    [InlineData("mfa", "0")]
    public void Anything_less_than_the_step_up_level_with_a_real_time_is_no_second_factor(string acr, string authTime)
    {
        ApproverClaims.From(Person(("sub", "a"), ("acr", acr), ("auth_time", authTime)), Options("mfa"))!.StepUpAt.Should().BeNull();
    }

    [Fact]
    public void Nobody_has_a_second_factor_when_no_level_is_configured()
    {
        ApproverClaims.From(Person(("sub", "a"), ("acr", "mfa"), ("auth_time", "1793700000")), Options())!.StepUpAt.Should().BeNull();
    }

    // --- the name to show

    [Fact]
    public void The_name_shown_is_the_username_or_else_the_full_name_and_is_cut()
    {
        ApproverClaims.DisplayName(Person(("preferred_username", "alice"), ("name", "Alice Example"))).Should().Be("alice");
        ApproverClaims.DisplayName(Person(("name", "Alice Example"))).Should().Be("Alice Example");
        ApproverClaims.DisplayName(Person(("name", new string('x', 500)))).Should().HaveLength(100);
        ApproverClaims.DisplayName(Person(("name", "  "))).Should().BeNull();
        ApproverClaims.DisplayName(Person()).Should().BeNull();
    }

    // --- where to go after signing in

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/status", "/status")]
    [InlineData("/decisions?severity=high", "/decisions?severity=high")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("status", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/ok\r\nSet-Cookie: a=b", "/")]
    public void Only_a_path_of_this_application_is_a_place_to_return_to(string? asked, string expected)
    {
        ReturnUrl.Safe(asked).Should().Be(expected);
    }

    [Fact]
    public void A_path_that_is_too_long_is_not_followed()
    {
        ReturnUrl.Safe("/" + new string('a', 3000)).Should().Be("/");
    }

    // --- a change comes from this application's own page

    [Theory]
    [InlineData("GET", null, null, true)]
    [InlineData("HEAD", "https://evil.example", "cross-site", true)]
    [InlineData("POST", "https://orchestrarr.example", null, true)]
    [InlineData("POST", "https://ORCHESTRARR.example", null, true)]
    [InlineData("PUT", "https://orchestrarr.example", "same-origin", true)]
    [InlineData("POST", "https://evil.example", null, false)]
    [InlineData("POST", "http://orchestrarr.example", null, false)]
    [InlineData("POST", "https://orchestrarr.example:8443", null, false)]
    [InlineData("POST", "null", null, false)]
    [InlineData("POST", null, "cross-site", false)]
    [InlineData("POST", null, "same-site", false)]
    [InlineData("POST", null, "same-origin", true)]
    [InlineData("DELETE", null, "none", true)]
    [InlineData("POST", null, null, true)]
    public void A_change_must_come_from_the_origin_of_the_application(string method, string? origin, string? fetchSite, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }
        if (fetchSite is not null)
        {
            context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        }

        SameOriginGuard.IsAllowed(context.Request, Valid()).Should().Be(allowed);
    }

    // --- the settings are checked when the host starts

    [Fact]
    public void Complete_settings_are_valid()
    {
        var act = () => Valid().Validate(development: false);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sso.example")]
    [InlineData("http://sso.example/realms/example")]
    [InlineData("ftp://sso.example")]
    public void The_authority_must_be_an_https_address(string? authority)
    {
        var options = Valid();
        options.Authority = authority;

        var act = () => options.Validate(development: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SignIn:Authority*");
    }

    [Fact]
    public void Clear_text_is_for_the_loopback_of_a_development_machine_only()
    {
        var local = Valid();
        local.Authority = "http://localhost:8081/realms/example";
        local.PublicUrl = "http://localhost:5178";

        var inDevelopment = () => local.Validate(development: true);
        var inProduction = () => local.Validate(development: false);

        inDevelopment.Should().NotThrow();
        inProduction.Should().Throw<InvalidOperationException>();

        var remote = Valid();
        remote.Authority = "http://sso.example/realms/example";
        var remoteInDevelopment = () => remote.Validate(development: true);
        remoteInDevelopment.Should().Throw<InvalidOperationException>().WithMessage("*SignIn:Authority*");
    }

    [Theory]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    public void The_client_and_its_secret_are_required(string missing)
    {
        var options = Valid();
        if (missing == "ClientId")
        {
            options.ClientId = " ";
        }
        else
        {
            options.ClientSecret = null;
        }

        var act = () => options.Validate(development: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SignIn:ClientId*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("orchestrarr.example")]
    [InlineData("http://orchestrarr.example")]
    [InlineData("https://orchestrarr.example/app")]
    [InlineData("https://orchestrarr.example/?x=1")]
    [InlineData("https://user:pass@orchestrarr.example")]
    [InlineData("https://orchestrarr.example/#top")]
    public void The_public_address_is_an_origin(string? publicUrl)
    {
        var options = Valid();
        options.PublicUrl = publicUrl;

        var act = () => options.Validate(development: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SignIn:PublicUrl*");
    }

    [Theory]
    [InlineData("", "groups", 8)]
    [InlineData("two words", "groups", 8)]
    [InlineData("ops", "", 8)]
    [InlineData("ops", "groups", 0)]
    [InlineData("ops", "groups", 25)]
    public void The_group_the_claim_and_the_session_length_must_make_sense(string group, string claim, int hours)
    {
        var options = Valid();
        options.OperatorGroup = group;
        options.GroupsClaim = claim;
        options.SessionHours = hours;

        var act = () => options.Validate(development: false);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_callback_and_the_origin_come_from_the_configured_public_address()
    {
        var options = Valid();

        options.CallbackUrl.AbsoluteUri.Should().Be("https://orchestrarr.example/auth/callback");
        options.Secure.Should().BeTrue();
    }
}
