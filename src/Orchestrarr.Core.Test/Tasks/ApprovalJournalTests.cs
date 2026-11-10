using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Core.Test.Tasks;

public sealed class ApprovalJournalTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "orchestrarr-journal-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_folder, "approvals.jsonl");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_batch_that_was_written_is_found_with_its_digest()
    {
        var journal = new FileApprovalJournal(File);
        var id = Guid.NewGuid();

        (await journal.TryAppendAsync(id, "digest-1", "alice", Now, Ct)).Should().BeTrue();

        (await journal.CheckAsync(id, "digest-1", Ct)).Should().Be(JournalCheck.Found);
    }

    [Fact]
    public async Task A_batch_that_was_never_written_is_not_found()
    {
        var journal = new FileApprovalJournal(File);
        await journal.TryAppendAsync(Guid.NewGuid(), "digest-1", "alice", Now, Ct);

        (await journal.CheckAsync(Guid.NewGuid(), "digest-1", Ct)).Should().Be(JournalCheck.NotFound);
    }

    [Fact]
    public async Task A_missing_file_has_nothing_to_find()
    {
        (await new FileApprovalJournal(File).CheckAsync(Guid.NewGuid(), "x", Ct)).Should().Be(JournalCheck.NotFound);
    }

    [Fact]
    public async Task A_batch_with_another_digest_than_the_approved_one_is_a_mismatch()
    {
        var journal = new FileApprovalJournal(File);
        var id = Guid.NewGuid();
        await journal.TryAppendAsync(id, "digest-1", "alice", Now, Ct);

        (await journal.CheckAsync(id, "digest-2", Ct)).Should().Be(JournalCheck.Mismatch);
    }

    [Fact]
    public async Task A_line_changed_afterwards_breaks_the_chain_for_every_check()
    {
        var journal = new FileApprovalJournal(File);
        var id = Guid.NewGuid();
        await journal.TryAppendAsync(Guid.NewGuid(), "digest-0", "alice", Now, Ct);
        await journal.TryAppendAsync(id, "digest-1", "alice", Now, Ct);

        await System.IO.File.WriteAllTextAsync(File, (await System.IO.File.ReadAllTextAsync(File, Ct)).Replace("digest-0", "digest-9", StringComparison.Ordinal), Ct);

        (await journal.CheckAsync(id, "digest-1", Ct)).Should().Be(JournalCheck.ChainBroken);
        (await journal.TryAppendAsync(Guid.NewGuid(), "digest-2", "alice", Now, Ct)).Should().BeFalse("nothing is appended to a journal that was altered");
    }

    [Fact]
    public async Task A_line_removed_from_the_middle_breaks_the_chain()
    {
        var journal = new FileApprovalJournal(File);
        var id = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
        {
            await journal.TryAppendAsync(i == 2 ? id : Guid.NewGuid(), $"digest-{i}", "alice", Now, Ct);
        }

        var lines = await System.IO.File.ReadAllLinesAsync(File, Ct);
        await System.IO.File.WriteAllLinesAsync(File, [lines[0], lines[2]], Ct);

        (await journal.CheckAsync(id, "digest-2", Ct)).Should().Be(JournalCheck.ChainBroken);
    }

    [Fact]
    public async Task A_last_line_cut_short_by_a_crash_is_not_tampering_and_is_dropped_by_the_next_approval()
    {
        var journal = new FileApprovalJournal(File);
        var first = Guid.NewGuid();
        await journal.TryAppendAsync(first, "digest-0", "alice", Now, Ct);
        await System.IO.File.AppendAllTextAsync(File, "{\"sequence\":2,\"at\":\"2026-11-09T12:0", Ct);

        (await journal.CheckAsync(first, "digest-0", Ct)).Should().Be(JournalCheck.Found, "the chain up to the last complete line is intact");
        (await journal.CheckAsync(Guid.NewGuid(), "digest-x", Ct)).Should().Be(JournalCheck.NotFound);
        var second = Guid.NewGuid();
        (await journal.TryAppendAsync(second, "digest-1", "alice", Now, Ct)).Should().BeTrue();

        (await journal.CheckAsync(second, "digest-1", Ct)).Should().Be(JournalCheck.Found);
        (await journal.CheckAsync(first, "digest-0", Ct)).Should().Be(JournalCheck.Found);
        (await System.IO.File.ReadAllLinesAsync(File, Ct)).Should().HaveCount(2, "the fragment is gone");
    }

    [Fact]
    public async Task A_second_journal_on_the_same_file_reads_what_the_first_wrote()
    {
        var id = Guid.NewGuid();
        await new FileApprovalJournal(File).TryAppendAsync(id, "digest-1", "alice", Now, Ct);

        (await new FileApprovalJournal(File).CheckAsync(id, "digest-1", Ct)).Should().Be(JournalCheck.Found);
    }

    [Fact]
    public async Task Concurrent_writes_each_keep_their_place_in_one_chain()
    {
        var journal = new FileApprovalJournal(File);
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(ids.Select(id => journal.TryAppendAsync(id, "d", "alice", Now, Ct).AsTask()));

        foreach (var id in ids)
        {
            (await journal.CheckAsync(id, "d", Ct)).Should().Be(JournalCheck.Found);
        }
    }

    [Fact]
    public async Task A_journal_that_cannot_be_written_says_so()
    {
        // The "file" is a folder: it can be neither read as a file nor appended to
        Directory.CreateDirectory(File);

        (await new FileApprovalJournal(File).TryAppendAsync(Guid.NewGuid(), "d", "alice", Now, Ct)).Should().BeFalse();
        (await new FileApprovalJournal(File).CheckAsync(Guid.NewGuid(), "d", Ct)).Should().Be(JournalCheck.Unavailable);
    }
}
