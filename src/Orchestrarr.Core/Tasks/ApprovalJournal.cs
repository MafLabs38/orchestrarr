using System.Security.Cryptography;
using System.Text.Json;

namespace Orchestrarr.Core.Tasks;

/// <summary>What the journal says about a batch when the runner asks (the double control of ADR 0017).</summary>
public enum JournalCheck
{
    /// <summary>The journal holds this batch with this digest, and its chain is intact.</summary>
    Found,

    NotFound,

    /// <summary>The journal holds this batch with another digest: what was delivered is not what was approved.</summary>
    Mismatch,

    /// <summary>A line of the journal was altered or removed.</summary>
    ChainBroken,
    Unavailable,
}

/// <summary>One line of the journal: an approval that produced a batch, with the digest of the batch.</summary>
public sealed record JournalEntry(long Sequence, DateTimeOffset At, Guid BatchId, string Digest, string ApproverSubject, string PreviousHash, string Hash)
{
    public static string Genesis => new('0', 64);

    public static JournalEntry Create(string previousHash, long sequence, DateTimeOffset at, Guid batchId, string digest, string approver)
    {
        var moment = DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds());
        return new JournalEntry(sequence, moment, batchId, digest, approver, previousHash, Compute(previousHash, sequence, moment, batchId, digest, approver));
    }

    public static string Compute(string previousHash, long sequence, DateTimeOffset at, Guid batchId, string digest, string approver)
    {
        var writer = new CanonicalWriter();
        writer.Text(previousHash);
        writer.Long(sequence);
        writer.Seconds(at);
        writer.Text(batchId.ToString("D"));
        writer.Text(digest);
        writer.Text(approver);
        return Convert.ToHexString(SHA256.HashData(writer.ToArray())).ToLowerInvariant();
    }
}

/// <summary>
/// Where the control plane writes down, apart from its queue, each approval that made a batch. It is written before the batch is queued.
/// What it protects is that the runner can compare what it is handed with a record that a line written into the queue alone does not
/// reach: for that, production puts the journal where the control plane can append but not rewrite, and the runner can read.
/// </summary>
public interface IApprovalJournal
{
    /// <summary>False when the line could not be written: the approval is then refused, because the double control would have nothing to read.</summary>
    ValueTask<bool> TryAppendAsync(Guid batchId, string digest, string approverSubject, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>The runner's side of the journal: it reads, it never writes.</summary>
public interface IApprovalJournalReader
{
    ValueTask<JournalCheck> CheckAsync(Guid batchId, string digest, CancellationToken cancellationToken);
}

/// <summary>
/// A journal in a file, one JSON line per approval, each chained to the one before. It is both sides at once, for a sandbox where the control
/// plane and the runner share a disk; production gives the writer and the reader different rights, or a different place.
/// </summary>
public sealed class FileApprovalJournal(string path) : IApprovalJournal, IApprovalJournalReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();

    public ValueTask<bool> TryAppendAsync(Guid batchId, string digest, string approverSubject, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            try
            {
                var (entries, intact, cutShort) = Read();
                if (!intact)
                {
                    return ValueTask.FromResult(false);
                }
                var previous = entries.Count == 0 ? JournalEntry.Genesis : entries[^1].Hash;
                var entry = JournalEntry.Create(previous, entries.Count + 1L, at, batchId, digest, approverSubject);
                EnsureFolder();
                // The file is only ever appended to (production can make it append-only). A line cut short by a crash was never acknowledged;
                // it is ended where it stands and the reader skips it
                File.AppendAllText(path, (cutShort ? "\n" : string.Empty) + JsonSerializer.Serialize(entry, Json) + "\n");
                return ValueTask.FromResult(true);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException)
            {
                return ValueTask.FromResult(false);
            }
        }
    }

    public ValueTask<JournalCheck> CheckAsync(Guid batchId, string digest, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            try
            {
                var (entries, intact, _) = Read();
                if (!intact)
                {
                    return ValueTask.FromResult(JournalCheck.ChainBroken);
                }
                var found = entries.FirstOrDefault(entry => entry.BatchId == batchId);
                if (found is null)
                {
                    return ValueTask.FromResult(JournalCheck.NotFound);
                }
                return ValueTask.FromResult(string.Equals(found.Digest, digest, StringComparison.Ordinal) ? JournalCheck.Found : JournalCheck.Mismatch);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException)
            {
                return ValueTask.FromResult(JournalCheck.Unavailable);
            }
        }
    }

    // A line that does not parse is a write that was cut short by a crash (or is being made): it is no part of the chain and is skipped. It
    // cannot hide a change: a line that parses and does not chain, and a line missing from the middle, still break the chain, and a line
    // replaced by text that does not parse leaves its batch unknown, which is refused. CutShort says that the file does not end with a line feed.
    private (List<JournalEntry> Entries, bool Intact, bool CutShort) Read()
    {
        var entries = new List<JournalEntry>();
        if (Directory.Exists(path))
        {
            throw new IOException("The path of the journal is a folder.");
        }
        if (!File.Exists(path))
        {
            return (entries, true, false);
        }
        var text = File.ReadAllText(path);
        var previous = JournalEntry.Genesis;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            JournalEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<JournalEntry>(line, Json);
            }
            catch (JsonException)
            {
                continue;
            }
            if (entry is null
                || entry.Sequence != entries.Count + 1L
                || entry.PreviousHash != previous
                || entry.Hash != JournalEntry.Compute(previous, entry.Sequence, entry.At, entry.BatchId, entry.Digest, entry.ApproverSubject))
            {
                return (entries, false, false);
            }
            entries.Add(entry);
            previous = entry.Hash;
        }
        return (entries, true, text.Length > 0 && text[^1] != '\n');
    }

    private void EnsureFolder()
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }
    }
}

/// <summary>A journal that keeps nothing, for a host that has no runner yet: the double control then has nothing to compare with, and says so.</summary>
public sealed class NullApprovalJournal : IApprovalJournal
{
    public ValueTask<bool> TryAppendAsync(Guid batchId, string digest, string approverSubject, DateTimeOffset at, CancellationToken cancellationToken) =>
        ValueTask.FromResult(true);
}
