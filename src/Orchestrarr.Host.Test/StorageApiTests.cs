using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;

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

    private ForgeApiFactory Sqlite(Action<IServiceCollection>? overrides = null) => new()
    {
        Settings = new Dictionary<string, string?> { ["Storage:Provider"] = "Sqlite", ["Storage:Path"] = DatabasePath },
        ServiceOverrides = overrides,
    };

    private static Task<HttpResponseMessage> Approve(HttpClient client) =>
        client.PostAsJsonAsync("/api/v1/approvals", new { workItemId = "repository:example/shop", steps = new[] { "validate" }, pullRequests = new[] { new { number = 7, headSha = Head } } }, Ct);

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    [Fact]
    public async Task The_status_says_where_things_are_kept()
    {
        using var memory = new ForgeApiFactory();
        using var sqlite = Sqlite();

        var inMemory = (await Json(await memory.CreateClient().GetAsync("/api/v1/system/status", Ct))).GetProperty("storage").GetProperty("kind").GetString();
        var inFile = (await Json(await sqlite.CreateClient().GetAsync("/api/v1/system/status", Ct))).GetProperty("storage").GetProperty("kind").GetString();

        inMemory.Should().Be("memory");
        inFile.Should().Be("sqlite");
    }

    [Fact]
    public async Task An_approval_and_its_trace_survive_a_restart_of_the_host()
    {
        using (var first = Sqlite())
        {
            (await Approve(first.CreateClient())).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using var second = Sqlite();
        var client = second.CreateClient();

        var approvals = await Json(await client.GetAsync("/api/v1/approvals", Ct));
        var activity = await Json(await client.GetAsync("/api/v1/activity", Ct));
        approvals.EnumerateArray().Should().ContainSingle().Which.GetProperty("headSha").GetString().Should().Be(Head);
        activity.EnumerateArray().Should().ContainSingle().Which.GetProperty("event").GetString().Should().Be("approval.recorded");
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
    public async Task A_stop_that_cannot_be_released_for_certain_says_so_stays_engaged_and_leaves_no_trace_of_a_release()
    {
        using var factory = Sqlite(services =>
        {
            var stop = new CannotRelease();
            services.RemoveAll<IKillSwitchControl>();
            services.RemoveAll<IKillSwitch>();
            services.AddSingleton<IKillSwitchControl>(stop);
            services.AddSingleton<IKillSwitch>(stop);
        });
        var client = factory.CreateClient();

        var release = await client.PutAsJsonAsync("/api/v1/safety/kill-switch", new { engaged = false }, Ct);

        release.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Json(release)).GetProperty("error").GetString().Should().Be("killSwitchUnavailable");
        (await Json(await client.GetAsync("/api/v1/safety", Ct))).GetProperty("killSwitchEngaged").GetBoolean().Should().BeTrue();
        (await Json(await client.GetAsync("/api/v1/activity", Ct))).EnumerateArray().Should().BeEmpty();
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
    }

    [Theory]
    [InlineData("Storage:Provider", "Postgres")]
    [InlineData("Storage:Path", "")]
    [InlineData("Storage:Path", "   ")]
    public void A_persistent_store_with_nowhere_to_persist_stops_the_host(string key, string value)
    {
        using var factory = new ForgeApiFactory
        {
            Settings = new Dictionary<string, string?> { ["Storage:Provider"] = key == "Storage:Provider" ? value : "Sqlite", ["Storage:Path"] = key == "Storage:Path" ? value : DatabasePath },
        };

        var start = () => factory.CreateClient();

        start.Should().Throw<Exception>().Which.ToString().Should().Contain("Storage:");
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

    private sealed class CannotRelease : IKillSwitchControl
    {
        public bool Engaged => true;

        public void Set(bool engaged)
        {
            if (!engaged)
            {
                throw new KillSwitchUnavailableException();
            }
        }

        public ValueTask<bool> IsEngagedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }
}
