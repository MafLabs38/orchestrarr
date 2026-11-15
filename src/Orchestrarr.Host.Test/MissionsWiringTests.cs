using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Orchestrarr.Core.Missions;
using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

/// <summary>What the host wires by default is what decides whether a real mandate can be issued: these tests are on the wiring, not on the test doubles of the conductor tests.</summary>
public sealed class MissionsWiringTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("orchestrarr-wiring-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class ProductionFactory : AccountsApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
        }

        protected override string CookieSecurity => "SameAsRequest";
    }

    [Fact]
    public async Task The_demo_routes_do_not_exist_in_a_deployment_whatever_the_user()
    {
        using var factory = new ProductionFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Orchestrarr-Request", "test");
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { name = AccountsApiFactory.AdminName, password = AccountsApiFactory.AdminPassword }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        // Signed in as the administrator, who holds every permission: the route is not there
        (await client.PostAsync("/api/v1/dev/missions/demo", null, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsync("/api/v1/dev/controller/decide", null, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>().SelectMany(source => source.Endpoints).OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(route => route.RoutePattern.RawText).Should().NotContain(text => text != null && text.Contains("/dev/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_demo_routes_need_a_session_even_where_they_exist()
    {
        using var factory = new AccountsApiFactory();
        using var anonymous = factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Orchestrarr-Request", "test");

        (await anonymous.PostAsync("/api/v1/dev/missions/demo", null, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync("/api/v1/dev/controller/decide", null, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_missions_api_only_reads()
    {
        using var factory = new AccountsApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Orchestrarr-Request", "test");
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { name = AccountsApiFactory.AdminName, password = AccountsApiFactory.AdminPassword }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        foreach (var path in new[] { "/api/v1/missions", "/api/v1/missions/any" })
        {
            foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
            {
                var response = await client.SendAsync(new HttpRequestMessage(method, path), TestContext.Current.CancellationToken);
                response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
            }
        }
    }

    [Fact]
    public void The_production_wiring_issues_no_mandate_because_no_approval_is_given_and_no_fact_is_known()
    {
        using var factory = new ProductionFactory();
        factory.Services.GetRequiredService<IApprovalSource>().Should().BeOfType<NoApprovals>();
        factory.Services.GetRequiredService<IFactsSource>().Should().BeOfType<ConservativeFacts>();
        factory.Services.GetRequiredService<IGrantSource>().MaxWorldFor("any-application").Should().BeNull("an application that is not listed has no power");
        factory.Services.GetRequiredService<IApprovalWithdrawals>().IsWithdrawn("anything").Should().BeTrue("the list that cannot be read counts as everything withdrawn");
        factory.Services.GetRequiredService<IApprovalSource>().For(new PlanEntry("p1", "shop", "class", "Replace", ["example/shop"], World.Release, new MissionBudget(20, 3, 50), new Validation("alice", null), DateTimeOffset.UnixEpoch)).Should().BeNull();

        var facts = factory.Services.GetRequiredService<IFactsSource>().Facts(new FactsQuery("p1", null, World.Git, new MissionFrame(["example/shop"], [], "Git:shop"), null, "abc"));

        facts.DiffIsClean.Should().BeNull();
        facts.CiGreenOnExactCommit.Should().BeNull();
        facts.ArtifactIsTheOneProvedInStaging.Should().BeNull();
        facts.WayBackKnown.Should().BeNull();
        facts.WorkerClaimMatchesFacts.Should().BeFalse();
        facts.BudgetAvailable.Should().BeFalse();
    }

    [Fact]
    public void Without_the_public_key_of_the_authority_no_certificate_is_valid_and_with_it_only_its_certificates_are()
    {
        var time = TimeProvider.System;
        using var authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificate = new ApprovalCertificate("c1", CertificateKind.Problem, "p1", "shop", ["example/shop"], World.Git, new MissionBudget(20, 3, 50), time.GetUtcNow().AddMinutes(-1), time.GetUtcNow().AddHours(1), "alice", "n", [], 8);
        var signed = new SignedApproval(certificate, authorityKey.SignData(certificate.CanonicalBytes(), HashAlgorithmName.SHA256));
        var mandate = new Mandate("m1", "mi1", "p1", World.Git, "runner-git", [GatedAct.OpenPullRequest], new MissionFrame(["example/shop"], [], "Git:shop"), new MissionBudget(10, 2, 20), time.GetUtcNow(), time.GetUtcNow().AddMinutes(30), MandateSource.PlanValidation, "n1", "c1", "shop", "class");

        using var nobody = AuthorityKey.Verifier(null, time);
        nobody.Check(signed, mandate).Should().Be(ApprovalRefusal.BadSignature, "no key configured: no certificate is valid");

        var file = Path.Combine(_folder, "authority.pub");
        File.WriteAllBytes(file, authorityKey.ExportSubjectPublicKeyInfo());
        using var verifier = AuthorityKey.Verifier(file, time);
        verifier.Check(signed, mandate).Should().Be(ApprovalRefusal.None);

        using var missing = AuthorityKey.Verifier(Path.Combine(_folder, "missing.pub"), time);
        missing.Check(signed, mandate).Should().Be(ApprovalRefusal.BadSignature, "a key file that is named and not there is no key: nothing is valid");

        var garbage = Path.Combine(_folder, "garbage.pub");
        File.WriteAllText(garbage, "this is not a key");
        var broken = () => AuthorityKey.Verifier(garbage, time);
        broken.Should().Throw<CryptographicException>("a key file that cannot be read stops the host, loudly, instead of leaving it with no way to verify");
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void The_signing_key_is_created_for_its_owner_only_is_the_same_after_a_restart_and_a_corrupt_file_is_never_replaced_silently()
    {
        var file = Path.Combine(_folder, "keys", "mandate-signing.key");

        using var first = SigningKey.Open(file);
        File.GetUnixFileMode(file).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var again = SigningKey.Open(file);
        again.PublicKey().Should().Equal(first.PublicKey());

        File.WriteAllText(file, "corrupt");
        var open = () => SigningKey.Open(file);
        open.Should().Throw<CryptographicException>("the host stops instead of signing with a new key nobody knows");
        File.ReadAllText(file).Should().Be("corrupt", "the file was not replaced");
    }
}
