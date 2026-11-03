using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Orchestrarr.Host.SignIn;

namespace Orchestrarr.Host.Test;

public sealed class SignInUnitTests
{
    private static SignInOptions Valid() => new()
    {
        Authority = "https://sso.example/realms/example",
        ClientId = "orchestrarr",
        ClientSecret = "not-a-real-secret",
        PublicUrl = "https://orchestrarr.example",
    };

    private static SignInSettings Settings(string group = "orchestrarr-operators", string claim = "groups", string? stepUpAcr = null)
    {
        var options = Valid();
        options.OperatorGroup = group;
        options.GroupsClaim = claim;
        options.StepUpAcr = stepUpAcr;
        return options.Validate(allowCleartextOnLoopback: false);
    }

    private static ClaimsPrincipal Person(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(claim => new Claim(claim.Type, claim.Value)), authenticationType: "test"));

    // --- who is an operator

    [Fact]
    public void Nobody_signed_in_is_nobody_even_with_a_subject_and_the_group()
    {
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice-1"), new Claim("groups", "orchestrarr-operators")]));

        ApproverClaims.From(unauthenticated, Settings()).Should().BeNull();
    }

    [Fact]
    public void A_member_of_the_operator_group_is_an_operator_and_a_person()
    {
        var approver = ApproverClaims.From(Person(("sub", "alice-1"), ("groups", "orchestrarr-operators")), Settings());

        approver.Should().NotBeNull();
        approver.Subject.Should().Be("alice-1");
        approver.IsOperator.Should().BeTrue();
        approver.IsPerson.Should().BeTrue();
    }

    [Fact]
    public void The_groups_of_a_person_are_often_several_claims_and_one_of_them_is_enough()
    {
        ApproverClaims.From(Person(("sub", "a"), ("groups", "staff"), ("groups", "orchestrarr-operators")), Settings())!.IsOperator.Should().BeTrue();
        ApproverClaims.From(Person(("sub", "a"), ("groups", "staff"), ("groups", "other")), Settings())!.IsOperator.Should().BeFalse();
    }

    [Fact]
    public void Groups_sent_as_one_json_array_count_too()
    {
        var claim = new Claim("groups", "[\"staff\",\"orchestrarr-operators\"]", JsonClaimValueTypes.JsonArray);
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice-1"), claim], "test"));

        ApproverClaims.From(user, Settings())!.IsOperator.Should().BeTrue();
    }

    [Fact]
    public void A_broken_json_array_makes_nobody_an_operator()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice-1"), new Claim("groups", "[not json", JsonClaimValueTypes.JsonArray)], "test"));

        ApproverClaims.From(user, Settings())!.IsOperator.Should().BeFalse();
    }

    [Theory]
    [InlineData("staff")]
    [InlineData("orchestrarr-operators-extra")]
    [InlineData("Orchestrarr-Operators")]
    [InlineData("a/orchestrarr-operators")]
    [InlineData("/orchestrarr-operators")]
    [InlineData("/team/orchestrarr-operators")]
    public void A_group_is_compared_exactly_so_that_a_group_of_the_same_name_elsewhere_in_a_tree_does_not_count(string group)
    {
        ApproverClaims.From(Person(("sub", "alice-1"), ("groups", group)), Settings())!.IsOperator.Should().BeFalse();
    }

    [Fact]
    public void A_provider_that_sends_the_full_path_of_a_group_is_configured_with_it()
    {
        var settings = Settings(group: "/orchestrarr-operators");

        ApproverClaims.From(Person(("sub", "a"), ("groups", "/orchestrarr-operators")), settings)!.IsOperator.Should().BeTrue();
        ApproverClaims.From(Person(("sub", "a"), ("groups", "/team/orchestrarr-operators")), settings)!.IsOperator.Should().BeFalse();
    }

    [Fact]
    public void The_claim_that_lists_the_groups_is_the_configured_one()
    {
        var settings = Settings(group: "ops", claim: "roles");

        ApproverClaims.From(Person(("sub", "a"), ("roles", "ops")), settings)!.IsOperator.Should().BeTrue();
        ApproverClaims.From(Person(("sub", "a"), ("groups", "ops")), settings)!.IsOperator.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("alice 1")]
    public void An_identity_without_a_usable_subject_is_nobody(string? subject)
    {
        var claims = subject is null ? [("groups", "orchestrarr-operators")] : new[] { ("sub", subject), ("groups", "orchestrarr-operators") };

        ApproverClaims.From(Person(claims), Settings()).Should().BeNull();
    }

    [Fact]
    public void The_name_is_not_a_subject()
    {
        ApproverClaims.From(Person(("preferred_username", "alice"), ("groups", "orchestrarr-operators")), Settings()).Should().BeNull();
    }

    [Fact]
    public void A_member_of_a_group_is_told_from_its_claim()
    {
        var user = Person(("groups", "readers"), ("groups", "staff"));

        ApproverClaims.IsMember(user, "groups", "readers").Should().BeTrue();
        ApproverClaims.IsMember(user, "groups", "reader").Should().BeFalse();
        ApproverClaims.IsMember(user, "roles", "readers").Should().BeFalse();
    }

    // --- the second factor

    [Fact]
    public void A_session_at_the_step_up_level_carries_the_moment_of_the_second_factor()
    {
        var approver = ApproverClaims.From(Person(("sub", "a"), ("acr", "mfa"), ("auth_time", "1793700000")), Settings(stepUpAcr: "mfa"));

        approver!.StepUpAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1793700000));
    }

    [Theory]
    [InlineData("password", "1793700000")]
    [InlineData("mfa", "yesterday")]
    [InlineData("mfa", "0")]
    [InlineData("mfa", "-5")]
    [InlineData("mfa", "253402300800")]
    [InlineData("mfa", "99999999999999")]
    [InlineData("mfa", "9999999999999999999999")]
    public void Anything_less_than_the_step_up_level_with_a_real_time_is_no_second_factor_and_never_an_error(string acr, string authTime)
    {
        ApproverClaims.From(Person(("sub", "a"), ("acr", acr), ("auth_time", authTime)), Settings(stepUpAcr: "mfa"))!.StepUpAt.Should().BeNull();
    }

    [Fact]
    public void A_session_without_the_time_of_its_authentication_is_no_second_factor()
    {
        ApproverClaims.From(Person(("sub", "a"), ("acr", "mfa")), Settings(stepUpAcr: "mfa"))!.StepUpAt.Should().BeNull();
    }

    [Fact]
    public void The_latest_moment_there_is_still_counts()
    {
        ApproverClaims.From(Person(("sub", "a"), ("acr", "mfa"), ("auth_time", "253402300799")), Settings(stepUpAcr: "mfa"))!.StepUpAt
            .Should().Be(DateTimeOffset.FromUnixTimeSeconds(253402300799));
    }

    [Fact]
    public void Nobody_has_a_second_factor_when_no_level_is_configured()
    {
        ApproverClaims.From(Person(("sub", "a"), ("acr", "mfa"), ("auth_time", "1793700000")), Settings())!.StepUpAt.Should().BeNull();
    }

    // --- the name to show

    [Theory]
    [InlineData("alice", "Alice Example", "alice")]
    [InlineData("  ", "Alice Example", "Alice Example")]
    [InlineData(null, "Alice Example", "Alice Example")]
    [InlineData(null, null, null)]
    [InlineData("  ", "  ", null)]
    public void The_name_shown_is_the_username_or_else_the_full_name(string? username, string? fullName, string? expected)
    {
        var claims = new List<(string, string)>();
        if (username is not null)
        {
            claims.Add(("preferred_username", username));
        }
        if (fullName is not null)
        {
            claims.Add(("name", fullName));
        }

        ApproverClaims.DisplayName(Person([.. claims])).Should().Be(expected);
    }

    [Fact]
    public void A_long_name_is_cut()
    {
        ApproverClaims.DisplayName(Person(("name", new string('x', 500)))).Should().HaveLength(100);
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

    private static bool Allowed(string method, string? origin, string? fetchSite, string? sessionCookie = null)
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
        if (sessionCookie is not null)
        {
            context.Request.Headers.Cookie = $"{sessionCookie}=abc";
        }
        return SameOriginGuard.IsAllowed(context.Request, Settings());
    }

    [Theory]
    [InlineData("GET", "https://evil.example", "cross-site")]
    [InlineData("OPTIONS", null, null)]
    public void A_read_is_never_refused(string method, string? origin, string? fetchSite)
    {
        Allowed(method, origin, fetchSite).Should().BeTrue();
    }

    [Theory]
    [InlineData("POST", "https://orchestrarr.example")]
    [InlineData("post", "https://ORCHESTRARR.example")]
    public void A_change_from_the_origin_of_the_application_goes_through(string method, string origin)
    {
        Allowed(method, origin, null).Should().BeTrue();
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://orchestrarr.example")]
    [InlineData("https://orchestrarr.example:8443")]
    [InlineData("null")]
    public void A_change_from_any_other_origin_is_refused_whatever_else_the_browser_says(string origin)
    {
        Allowed("POST", origin, "same-origin").Should().BeFalse();
    }

    [Theory]
    [InlineData("cross-site", false)]
    [InlineData("same-site", false)]
    [InlineData("Same-Origin", true)]
    [InlineData("none", true)]
    public void Without_an_origin_the_browser_says_where_a_change_comes_from(string fetchSite, bool allowed)
    {
        Allowed("POST", null, fetchSite).Should().Be(allowed);
    }

    [Fact]
    public void A_client_that_says_nothing_has_no_session_to_abuse_but_a_request_that_carries_the_session_and_says_nothing_is_not_trusted()
    {
        Allowed("POST", null, null).Should().BeTrue();
        Allowed("POST", null, null, sessionCookie: "__Host-orchestrarr.session").Should().BeFalse();
    }

    // --- the settings are checked when the host starts

    [Fact]
    public void Complete_options_make_settings_that_say_where_the_provider_and_the_application_are()
    {
        var settings = Valid().Validate(allowCleartextOnLoopback: false);

        settings.CallbackUrl.AbsoluteUri.Should().Be("https://orchestrarr.example/auth/callback");
        settings.Secure.Should().BeTrue();
        settings.SessionCookieName.Should().StartWith("__Host-");
        settings.SessionLifetime.Should().Be(TimeSpan.FromHours(8));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sso.example")]
    [InlineData("http://sso.example/realms/example")]
    public void The_authority_must_be_an_https_address(string? authority)
    {
        var options = Valid();
        options.Authority = authority;

        var act = () => options.Validate(allowCleartextOnLoopback: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SignIn:Authority*");
    }

    [Fact]
    public void Clear_text_is_for_the_loopback_of_a_development_machine_only()
    {
        var local = Valid();
        local.Authority = "http://localhost:8081/realms/example";
        local.PublicUrl = "http://localhost:5178";

        var inDevelopment = local.Validate(allowCleartextOnLoopback: true);
        var inProduction = () => local.Validate(allowCleartextOnLoopback: false);

        inDevelopment.Secure.Should().BeFalse();
        inDevelopment.SessionCookieName.Should().Be("orchestrarr.session");
        inProduction.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("http://sso.example/realms/example")]
    [InlineData("http://localhost.evil.example/realms/example")]
    public void Clear_text_to_somewhere_else_than_the_loopback_is_refused_even_in_development(string authority)
    {
        var options = Valid();
        options.Authority = authority;

        var act = () => options.Validate(allowCleartextOnLoopback: true);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SignIn:Authority*");
    }

    [Fact]
    public void Clear_text_for_the_public_address_somewhere_else_than_the_loopback_is_refused_even_in_development()
    {
        var options = Valid();
        options.PublicUrl = "http://orchestrarr.example";

        var act = () => options.Validate(allowCleartextOnLoopback: true);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SignIn:PublicUrl*");
    }

    [Fact]
    public void An_ipv6_loopback_is_a_loopback()
    {
        var options = Valid();
        options.PublicUrl = "http://[::1]:5178";

        var act = () => options.Validate(allowCleartextOnLoopback: true);

        act.Should().NotThrow();
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

        var act = () => options.Validate(allowCleartextOnLoopback: false);

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

        var act = () => options.Validate(allowCleartextOnLoopback: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SignIn:PublicUrl*");
    }

    [Theory]
    [InlineData("", "readers", "groups", 8)]
    [InlineData("two words", "readers", "groups", 8)]
    [InlineData("/", "readers", "groups", 8)]
    [InlineData("ops", "two words", "groups", 8)]
    [InlineData("ops", "readers", "", 8)]
    [InlineData("ops", "readers", "groups", 0)]
    [InlineData("ops", "readers", "groups", 25)]
    public void The_groups_the_claim_and_the_session_length_must_make_sense(string group, string reader, string claim, int hours)
    {
        var options = Valid();
        options.OperatorGroup = group;
        options.ReaderGroup = reader;
        options.GroupsClaim = claim;
        options.SessionHours = hours;

        var act = () => options.Validate(allowCleartextOnLoopback: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SignIn:*");
    }

    public static TheoryData<string[]> BadScopes => new() { new[] { "profile" }, new[] { "openid", "pro file" }, new[] { "openid", " " } };

    [Theory]
    [MemberData(nameof(BadScopes))]
    public void The_scopes_include_openid_and_a_scope_has_no_space_in_it(string[] scopes)
    {
        var options = Valid();
        options.Scopes = [.. scopes];

        var act = () => options.Validate(allowCleartextOnLoopback: false);

        act.Should().Throw<InvalidOperationException>().WithMessage("*SignIn:Scopes*");
    }

    [Fact]
    public void A_provider_that_gates_the_groups_behind_a_scope_or_the_user_information_is_configured_for_it()
    {
        var options = Valid();
        options.Scopes = ["openid", "profile", "groups"];
        options.ClaimsFromUserInfo = true;

        var settings = options.Validate(allowCleartextOnLoopback: false);

        settings.Scopes.Should().Equal("openid", "profile", "groups");
        settings.ClaimsFromUserInfo.Should().BeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    public void The_session_length_has_its_bounds_inside(int hours)
    {
        var options = Valid();
        options.SessionHours = hours;

        options.Validate(allowCleartextOnLoopback: false).SessionLifetime.Should().Be(TimeSpan.FromHours(hours));
    }
}
