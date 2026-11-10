using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Test;

public sealed class StorageApiTests : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly string Head = new('c', 40);

    private readonly string _folder = Directory.CreateTempSubdirectory("orchestrarr-host-").FullName;

    private string DatabasePath => Path.Combine(_folder, "data", "orchestrarr.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    private ForgeApiFactory Sqlite(Action<IServiceCollection>? overrides = null, Dictionary<string, string?>? settings = null) => new()
    {
        Settings = new Dictionary<string, string?> { ["Storage:Provider"] = "Sqlite", ["Storage:Path"] = DatabasePath }.Concat(settings ?? []).ToDictionary(),
        ServiceOverrides = overrides,
    };

    private static Task<HttpResponseMessage> Approve(HttpClient client, string repository = "example/shop") =>
        client.PostAsJsonAsync("/api/v1/approvals", new { workItemId = $"repository:{repository}", steps = new[] { "validate" }, pullRequests = new[] { new { number = 7, headSha = Head } } }, Ct);

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task An_approval_and_its_trace_survive_a_restart_of_the_host()
    {
        using (var first = Sqlite())
        {
            (await Approve(first.CreateClient())).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using var second = Sqlite();
        var client = second.CreateClient();

        (await Json(await client.GetAsync("/api/v1/system/status", Ct))).GetProperty("storage").GetProperty("kind").GetString().Should().Be("sqlite");
        var approvals = await Json(await client.GetAsync("/api/v1/approvals", Ct));
        var activity = await Json(await client.GetAsync("/api/v1/audit", Ct));
        approvals.EnumerateArray().Should().ContainSingle().Which.GetProperty("headSha").GetString().Should().Be(Head);
        activity.EnumerateArray().Select(entry => entry.GetProperty("event").GetString()).Should().BeEquivalentTo("approval.recorded", "batch.queued");
        (await Json(await client.GetAsync("/api/v1/runs", Ct))).EnumerateArray().Should().ContainSingle().Which.GetProperty("state").GetString().Should().Be("waiting");
        (await Approve(client)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_global_stop_stays_engaged_after_a_restart_and_nothing_is_approved()
    {
        using (var first = Sqlite())
        {
            (await first.CreateClient().PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var second = Sqlite();
        var client = second.CreateClient();

        (await Json(await client.GetAsync("/api/v1/safety", Ct))).GetProperty("killSwitchEngaged").GetBoolean().Should().BeTrue();
        (await Approve(client)).StatusCode.Should().Be(HttpStatusCode.Locked);
        (await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = false }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(await client.GetAsync("/api/v1/safety", Ct))).GetProperty("killSwitchEngaged").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task When_the_file_becomes_unreadable_nothing_is_approved_and_the_stop_counts_as_engaged()
    {
        using var factory = Sqlite();
        var client = factory.CreateClient();
        (await client.GetAsync("/api/v1/system/status", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(DatabasePath)!))
        {
            File.Delete(file);
        }
        await File.WriteAllTextAsync(DatabasePath, "this is not a database", Ct);

        var approve = await Approve(client);
        var safety = await Json(await client.GetAsync("/api/v1/safety", Ct));

        approve.StatusCode.Should().Be(HttpStatusCode.Locked);
        safety.GetProperty("killSwitchEngaged").GetBoolean().Should().BeTrue();
        var queue = await client.GetAsync("/api/v1/approvals", Ct);
        var trail = await client.GetAsync("/api/v1/audit", Ct);
        queue.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "an unreadable queue is an error, never an empty list");
        trail.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "an unreadable trail is an error, never an empty list");
        (await Json(queue)).GetProperty("error").GetString().Should().Be("storageUnavailable");
        (await client.GetAsync("/api/v1/system/status", Ct)).StatusCode.Should().Be(HttpStatusCode.OK, "the status still answers, and says the stop is engaged");

        var release = await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = false }, Ct);
        var engage = await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct);

        release.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Json(release)).GetProperty("error").GetString().Should().Be("killSwitchUnavailable");
        engage.StatusCode.Should().Be(HttpStatusCode.OK, "engaging always works");
        (await Json(engage)).GetProperty("saved").GetBoolean().Should().BeFalse("it is held in memory, and would not survive a restart");
    }

    [Fact]
    public void A_persistent_store_with_nowhere_to_persist_stops_the_host()
    {
        using var factory = new ForgeApiFactory
        {
            Settings = new Dictionary<string, string?> { ["Storage:Provider"] = "Sqlite", ["Storage:Path"] = "" },
        };

        var start = () => factory.CreateClient();

        start.Should().Throw<InvalidOperationException>().Which.ToString().Should().Contain("Storage:Path");
    }

    [Fact]
    public async Task A_file_that_cannot_be_used_stops_the_host_at_its_start_and_not_at_the_first_approval()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await File.WriteAllTextAsync(DatabasePath, "this is not a database", Ct);
        using var factory = Sqlite();

        var start = () => factory.CreateClient();

        start.Should().Throw<Exception>();
    }

    [Fact]
    public async Task The_sandbox_in_memory_says_so_in_its_status()
    {
        using var factory = new ForgeApiFactory();

        (await Json(await factory.CreateClient().GetAsync("/api/v1/system/status", Ct))).GetProperty("storage").GetProperty("kind").GetString().Should().Be("memory");
    }

    [Fact]
    public async Task A_database_that_is_created_outside_development_starts_with_the_stop_engaged_and_says_why()
    {
        using var factory = new ForgeApiFactory { EnvironmentName = "Testing", Settings = SqliteSettings() };
        var client = factory.CreateClient();

        (await Json(await client.GetAsync("/api/v1/safety", Ct))).GetProperty("killSwitchEngaged").GetBoolean().Should().BeTrue();
        // Approving is off in this host, so the trail is read from the file the host wrote at its start
        var trail = new SqliteAuditLog(SqliteDatabase.Open(DatabasePath)).List();
        trail.Should().ContainSingle().Which.Event.Should().Be(AuditEvents.InstanceCreated);
    }

    [Fact]
    public async Task A_database_that_is_created_in_development_starts_released()
    {
        using var factory = Sqlite();

        (await Json(await factory.CreateClient().GetAsync("/api/v1/safety", Ct))).GetProperty("killSwitchEngaged").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_stop_that_was_engaged_and_could_not_be_saved_says_it_is_not_saved()
    {
        using var factory = Sqlite(services =>
        {
            var stop = new HeldInMemory();
            services.RemoveAll<IKillSwitchControl>();
            services.RemoveAll<IKillSwitch>();
            services.AddSingleton<IKillSwitchControl>(stop);
            services.AddSingleton<IKillSwitch>(stop);
        });
        var client = factory.CreateClient();

        var engage = await Json(await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct));
        var status = (await Json(await client.GetAsync("/api/v1/system/status", Ct))).GetProperty("approvals");

        engage.GetProperty("killSwitchEngaged").GetBoolean().Should().BeTrue();
        engage.GetProperty("saved").GetBoolean().Should().BeFalse();
        status.GetProperty("killSwitchSaved").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_release_that_cannot_be_traced_is_refused_and_the_stop_stays_engaged_in_the_file()
    {
        // A trail of two places, both taken by engagements: the release has nowhere to be traced
        using var factory = Sqlite(settings: new() { ["Approvals:AuditCapacity"] = "2" });
        var client = factory.CreateClient();
        (await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var release = await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = false }, Ct);

        release.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Json(release)).GetProperty("error").GetString().Should().Be("auditUnavailable");
        (await Json(await client.GetAsync("/api/v1/safety", Ct))).GetProperty("killSwitchEngaged").GetBoolean().Should().BeTrue();
        new SqliteKillSwitch(SqliteDatabase.Open(DatabasePath), 2).Engaged.Should().BeTrue("the refused release left nothing in the file");
    }

    [Fact]
    public async Task Engaging_with_a_full_trail_still_engages()
    {
        using var factory = Sqlite(settings: new() { ["Approvals:AuditCapacity"] = "2" });
        var client = factory.CreateClient();
        await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct);
        await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct);

        var again = await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct);

        again.StatusCode.Should().Be(HttpStatusCode.OK, "the trail is full, and the stop applies all the same");
        (await Json(again)).GetProperty("killSwitchEngaged").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task The_lists_answer_the_newest_entries_and_a_limit_never_drops_below_one()
    {
        using var factory = Sqlite();
        var client = factory.CreateClient();
        foreach (var engaged in new[] { true, false, true, false })
        {
            (await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var two = (await Json(await client.GetAsync("/api/v1/audit?limit=2", Ct))).EnumerateArray().ToList();
        var floor = (await Json(await client.GetAsync("/api/v1/audit?limit=0", Ct))).EnumerateArray().ToList();
        var all = (await Json(await client.GetAsync("/api/v1/audit", Ct))).EnumerateArray().ToList();

        two.Select(entry => entry.GetProperty("event").GetString()).Should().Equal("killswitch.released", "killswitch.engaged");
        two.Select(entry => entry.GetProperty("id").GetInt64()).Should().Equal(4, 3);
        all.Select(entry => entry.GetProperty("id").GetInt64()).Should().Equal(4, 3, 2, 1);
        floor.Should().ContainSingle();
        all.Should().HaveCount(4);
    }

    [Fact]
    public async Task Approving_is_limited_per_person_so_that_a_loop_of_refusals_cannot_fill_the_trail_and_the_stop_never_is()
    {
        using var factory = Sqlite(settings: new() { ["Approvals:ApprovalsLimit"] = "2", ["Approvals:ApprovalsWindowMinutes"] = "600" });
        var client = factory.CreateClient();

        var first = await Approve(client);
        var second = await Approve(client);
        var third = await Approve(client);
        var engage = await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = true }, Ct);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict, "the same decision again is refused, and traced");
        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await Json(third)).GetProperty("error").GetString().Should().Be("tooManyRequests");
        engage.StatusCode.Should().Be(HttpStatusCode.OK, "the global stop is never limited");
        (await Json(await client.GetAsync("/api/v1/audit", Ct))).EnumerateArray().Should().HaveCount(4, "the third attempt left nothing in the trail: the approval and its batch for the first, the refusal for the second, the stop");
    }

    [Fact]
    public async Task An_in_memory_store_outside_development_starts_with_the_stop_engaged_and_says_why()
    {
        using var factory = new ForgeApiFactory { EnvironmentName = "Testing", Settings = new Dictionary<string, string?> { ["Storage:MemoryStartsEngaged"] = "true" } };
        var client = factory.CreateClient();

        (await Json(await client.GetAsync("/api/v1/safety", Ct))).GetProperty("killSwitchEngaged").GetBoolean().Should().BeTrue();
        (await Json(await client.GetAsync("/api/v1/audit", Ct))).EnumerateArray().Select(entry => entry.GetProperty("event").GetString()).Should().ContainSingle().Which.Should().Be("instance.created");
    }

    [Theory]
    [InlineData(null, 200)]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(1000, 1000)]
    [InlineData(1001, 1000)]
    [InlineData(int.MaxValue, 1000)]
    public void A_list_is_never_longer_than_the_maximum_whatever_is_asked(int? asked, int answered)
    {
        ApprovalEndpoints.PageSize(asked).Should().Be(answered);
    }

    [Fact]
    public async Task The_approvals_are_listed_newest_first_and_bounded_by_the_limit()
    {
        using var factory = Sqlite();
        var client = factory.CreateClient();
        (await Approve(client, "example/shop")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Approve(client, "example/billing-api")).StatusCode.Should().Be(HttpStatusCode.Created);

        var newest = (await Json(await client.GetAsync("/api/v1/approvals?limit=1", Ct))).EnumerateArray().ToList();
        var all = (await Json(await client.GetAsync("/api/v1/approvals", Ct))).EnumerateArray().ToList();

        newest.Should().ContainSingle().Which.GetProperty("repository").GetString().Should().Be("example/billing-api");
        all.Should().HaveCount(2);
    }

    // The host refuses to start: WebApplicationFactory may report it as the exception itself or as a host that was disposed, so the details (which
    // exception, what the file holds) are proven by the tests of SqliteStartup and of the lock, and here only that the host does not start.
    [Fact]
    public async Task A_second_host_on_the_same_file_does_not_start_and_the_first_goes_on()
    {
        using var first = Sqlite();
        using var firstClient = first.CreateClient();
        using var second = Sqlite();

        var start = () => second.CreateClient();

        start.Should().Throw<Exception>();
        (await firstClient.GetAsync("/health", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void A_host_that_is_refused_by_the_lock_has_not_created_the_database()
    {
        using var held = SqliteWriterLock.Acquire(DatabasePath);
        using var factory = Sqlite();

        var start = () => factory.CreateClient();

        start.Should().Throw<Exception>();
        File.Exists(DatabasePath).Should().BeFalse("the lock comes before the database is created, migrated or written");
    }

    [Fact]
    public void No_route_of_the_api_is_named_like_something_a_content_blocker_refuses()
    {
        using var factory = new ForgeApiFactory();
        factory.CreateClient();

        var routes = factory.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty).ToList();

        routes.Should().Contain("/api/v1/audit");
        routes.Should().NotContain(route => route.Contains("activity", StringComparison.OrdinalIgnoreCase), "a browser extension refused /api/v1/activity: the screen showed \"Failed to fetch\" and the server never saw the request");
    }

    private Dictionary<string, string?> SqliteSettings() => new() { ["Storage:Provider"] = "Sqlite", ["Storage:Path"] = DatabasePath };

    private sealed class HeldInMemory : IKillSwitchControl
    {
        private volatile bool _engaged;

        public bool Engaged => _engaged;

        public KillSwitchState State() => new(_engaged, Saved: !_engaged);

        public ValueTask<bool> IsEngagedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_engaged);

        public ValueTask<KillSwitchChange> ChangeAsync(bool engaged, AuditEntry entry, CancellationToken cancellationToken)
        {
            _engaged = engaged;
            return ValueTask.FromResult(KillSwitchChange.AppliedUntraced);
        }
    }
}
