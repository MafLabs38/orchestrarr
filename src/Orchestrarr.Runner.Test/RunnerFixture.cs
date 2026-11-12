using Microsoft.Extensions.Time.Testing;
using Orchestrarr.Core.SecondFactor;
using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Runner.Test;

/// <summary>A runner's world for a test: its own file, a journal, an operator who is enrolled, a clock, and the pipeline over them.</summary>
internal sealed class RunnerFixture : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 9, 12, 0, 10, TimeSpan.Zero);
    public static readonly byte[] Secret = [9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
    public static readonly string Head = new('a', 40);
    public static readonly TotpOptions Totp = new();

    private readonly string _folder = Directory.CreateTempSubdirectory("orchestrarr-runner-").FullName;

    public FakeTimeProvider Time { get; } = new(Start);

    public RunnerStore Store { get; }

    public FileApprovalJournal Journal { get; }

    public SecondFactorGuard Guard { get; }

    public RightsOptions Rights { get; set; } = new() { Kinds = [TaskKind.ValidatePullRequest, TaskKind.MergePullRequest], Repositories = ["example/*"], AcceptDelegations = true };

    public RunnerFixture()
    {
        Store = new RunnerStore(Path.Combine(_folder, "runner.db"));
        Journal = new FileApprovalJournal(Path.Combine(_folder, "journal.jsonl"));
        Guard = new SecondFactorGuard(Store, Store, Time, new SecondFactorOptions());
        Store.Enrol("alice", Secret, 0, Start);
    }

    public string Folder => _folder;

    public RunnerPipeline Pipeline(IEnumerable<ITaskHandler>? handlers = null, RightsOptions? rights = null, bool requireSecondFactor = true) =>
        new(Store, Guard, Journal, rights ?? Rights, handlers ?? [new DryRunTaskHandler(TaskKind.ValidatePullRequest), new DryRunTaskHandler(TaskKind.MergePullRequest)], Time, "runner-1", requireSecondFactor);

    public string CodeAt(int stepOffset = 0) => SecondFactor.TotpMath.Code(Secret, SecondFactor.TotpMath.StepAt(Time.GetUtcNow(), Totp) + stepOffset, Totp);

    public static TaskBatch Batch(string repository = "example/app", string subject = "alice", Guid? delegation = null, DateTimeOffset? expires = null, int pullRequest = 1, TaskKind[]? kinds = null)
    {
        var approval = Guid.NewGuid();
        var tasks = (kinds ?? [TaskKind.ValidatePullRequest, TaskKind.MergePullRequest])
            .Select((kind, index) => new TaskSpec(Guid.NewGuid(), index + 1, kind, repository, pullRequest, Head, approval))
            .ToList();
        return new TaskBatch(Guid.NewGuid(), "repository:" + repository, subject, Start, expires ?? Start.AddHours(1), tasks, delegation);
    }

    /// <summary>Writes the batch in the journal, as the control plane does when it approves.</summary>
    public async Task<TaskBatch> Approved(TaskBatch batch)
    {
        (await Journal.TryAppendAsync(batch.Id, batch.Digest(), batch.ApproverSubject, Start, TestContext.Current.CancellationToken)).Should().BeTrue();
        return batch;
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}

// The namespace Orchestrarr.Runner.Test.SecondFactor would hide the type of Core: a short name for the arithmetic
internal static class SecondFactor
{
    public static class TotpMath
    {
        public static string Code(byte[] secret, long step, TotpOptions options) => Orchestrarr.Core.SecondFactor.Totp.Code(secret, step, options);

        public static long StepAt(DateTimeOffset time, TotpOptions options) => Orchestrarr.Core.SecondFactor.Totp.StepAt(time, options);
    }
}
