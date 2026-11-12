using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Decisions;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Host.Decisions;

namespace Orchestrarr.Host.Test;

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
        TimeProvider.System, NullLogger<HistoryConcluder>.Instance);

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
}
