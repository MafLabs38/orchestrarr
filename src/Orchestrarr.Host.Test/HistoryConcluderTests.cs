using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.Applications;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host.Decisions;
using Orchestrarr.Host.Live;
using Orchestrarr.Host.Tasks;

namespace Orchestrarr.Host.Test;

/// <summary>The table host with a clock that only the test moves, so that two readings of the sources are two moments.</summary>
public sealed class ClockedTableFactory : TableApiFactory
{
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}

/// <summary>The outcome of an approved line is written down by the concluding pass, never by a read of the history.</summary>
public sealed class HistoryConcluderTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static HistoryEntry Approved(Guid? approval) =>
        new(0, "pr:example/shop#2", "express 4.16.0 → 4.21.2", HistoryState.Approved, "example/shop", ProblemKind.DependencyUpdate, "express 4.16.0 → 4.21.2", "example/shop", 2, "abc", approval, null, DateTimeOffset.UnixEpoch, "alice", null);

    private static async Task<HttpClient> SignedIn(TableApiFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Orchestrarr-Request", "test");
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { name = AccountsApiFactory.AdminName, password = AccountsApiFactory.AdminPassword }, Ct)).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<string?> OutcomeOf(HttpClient client)
    {
        var lines = (await client.GetFromJsonAsync<JsonElement>("/api/v1/problems/history?state=approved", Ct)).GetProperty("lines");
        return lines.EnumerateArray().Single().GetProperty("outcome").GetString();
    }

    private static HistoryConcluder Concluder(TableApiFactory factory) => new(
        factory.Services.GetRequiredService<IProblemHistory>(), factory.Services.GetRequiredService<IApprovalStore>(), factory.Services.GetRequiredService<IBatchQueue>(),
        factory.Services.GetRequiredService<WorkItemCatalog>(), factory.Services.GetRequiredService<IApplicationCatalog>(), factory.Services.GetRequiredService<RunnerRegistry>(),
        factory.Services.GetRequiredService<RelevancePolicy>(), factory.Services.GetRequiredService<IAuditSink>(), factory.Services.GetRequiredService<ILiveNotifier>(), factory.Services.GetRequiredService<TimeProvider>(), NullLogger<HistoryConcluder>.Instance);

    [Fact]
    public async Task A_line_whose_approval_and_run_are_gone_is_ended_once_the_pass_has_run_and_a_read_writes_nothing()
    {
        using var factory = new TableApiFactory();
        using var client = await SignedIn(factory);
        var history = factory.Services.GetRequiredService<IProblemHistory>();
        history.Record(Approved(Guid.NewGuid()));

        (await OutcomeOf(client)).Should().Be("ended");
        history.Unconcluded(10).Should().ContainSingle("reading the history did not write the outcome");

        Concluder(factory).ConcludeOnce().Should().Be(1);

        history.Unconcluded(10).Should().BeEmpty();
        (await OutcomeOf(client)).Should().Be("ended");
        Concluder(factory).ConcludeOnce().Should().Be(0, "a line is concluded once");
    }

    [Fact]
    public async Task A_line_whose_approval_is_still_working_is_left_open_for_the_next_pass()
    {
        using var factory = new TableApiFactory();
        await factory.ConnectRunnerAsync();
        using var client = await SignedIn(factory);
        JsonElement table;
        do
        {
            table = await client.GetFromJsonAsync<JsonElement>("/api/v1/problems", Ct);
            await Task.Delay(50, Ct);
        }
        while (table.GetProperty("rows").EnumerateArray().Any(candidate => candidate.GetProperty("kind").GetString() == "pullRequestsNotRead"));
        var row = table.GetProperty("rows").EnumerateArray().First(candidate => candidate.GetProperty("id").GetString() == "pr:example/shop#2");
        var chosen = new { id = "pr:example/shop#2", headSha = row.GetProperty("headSha").GetString(), goesUpTo = row.GetProperty("goesUpTo").GetString(), stopBeforeProduction = false };
        (await client.PostAsJsonAsync("/api/v1/problems/approve", new { rows = new[] { chosen }, secondFactorCode = "123456", descriptionsVersion = table.GetProperty("descriptionsVersion").GetString() }, Ct)).EnsureSuccessStatusCode();

        Concluder(factory).ConcludeOnce().Should().Be(0, "a run that is not over is not an outcome");

        factory.Services.GetRequiredService<IProblemHistory>().Unconcluded(10).Should().ContainSingle();
        (await OutcomeOf(client)).Should().Be("inProgress");
    }

    [Fact]
    public async Task A_skip_ends_when_its_problem_is_gone_so_that_one_that_comes_back_is_seen_and_the_line_stays_in_the_history()
    {
        using var factory = new ClockedTableFactory();
        await factory.ConnectRunnerAsync();
        using var client = await SignedIn(factory);
        JsonElement table;
        do
        {
            table = await client.GetFromJsonAsync<JsonElement>("/api/v1/problems", Ct);
            await Task.Delay(50, Ct);
        }
        while (table.GetProperty("rows").EnumerateArray().Any(candidate => candidate.GetProperty("kind").GetString() == "pullRequestsNotRead"));
        var history = factory.Services.GetRequiredService<IProblemHistory>();
        var present = table.GetProperty("rows").EnumerateArray().First();
        history.Record(Skipped(present.GetProperty("id").GetString()!));
        history.Record(Skipped("finding:repository:example/gone:FailingCi"));

        var concluder = Concluder(factory);

        (await concluder.LapseSkipsOfGoneProblemsAsync(Ct)).Should().Be(0, "one reading that misses a problem is not enough");
        factory.Clock.Advance(TimeSpan.FromSeconds(10));
        (await client.GetAsync("/api/v1/problems?refresh=true", Ct)).EnsureSuccessStatusCode();
        (await concluder.LapseSkipsOfGoneProblemsAsync(Ct)).Should().Be(1, "only the skip of a problem that is missing from two readings ends");

        history.ActiveSkips().Should().ContainSingle().Which.RowId.Should().Be(present.GetProperty("id").GetString());
        var lines = (await client.GetFromJsonAsync<JsonElement>("/api/v1/problems/history?state=skipped", Ct)).GetProperty("lines").EnumerateArray().ToList();
        lines.Should().HaveCount(2);
        lines.Select(line => line.GetProperty("outcome").GetString()).Should().BeEquivalentTo("skipped", "ended");
    }

    [Fact]
    public async Task The_pass_reads_no_source_itself_so_nothing_ends_before_the_sources_were_read()
    {
        using var factory = new ClockedTableFactory();
        factory.Services.GetRequiredService<IProblemHistory>().Record(Skipped("finding:repository:example/gone:FailingCi"));

        (await Concluder(factory).LapseSkipsOfGoneProblemsAsync(Ct)).Should().Be(0);

        factory.Services.GetRequiredService<WorkItemCatalog>().Peek().Should().BeNull("the pass did not read the sources");
        factory.Services.GetRequiredService<IProblemHistory>().ActiveSkips().Should().ContainSingle();
    }

    private static HistoryEntry Skipped(string row) =>
        new(0, row, "1", HistoryState.Skipped, "example/shop", ProblemKind.FailingCi, "1", null, null, null, null, null, DateTimeOffset.UnixEpoch, "alice", null);
}
