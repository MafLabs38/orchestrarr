using System.Globalization;
using Microsoft.Data.Sqlite;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Storage;

/// <summary>
/// The approvals that wait for a launcher, kept in the SQLite file (ADR 0013). The store enforces the uniqueness of a decision itself, with a
/// unique key on repository, pull request, commit and steps, so that two processes cannot both record the same one. Adding is all or nothing.
/// The names of the steps are what is written: a name of <see cref="StepKind"/> is part of the file format. A file that cannot be used is
/// <see cref="StorageUnavailableException"/>, never an empty list.
/// </summary>
public sealed class SqliteApprovalStore(SqliteDatabase database, int capacity = StoreLimits.FileQueue) : IApprovalStore
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity), "The capacity must be greater than zero.");

    public int Free => SqliteDatabase.Guard(() =>
    {
        using var connection = database.Connect();
        return _capacity - Count(connection);
    });

    public bool Contains(Approval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        return SqliteDatabase.Guard(() =>
        {
            using var connection = database.Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM approvals WHERE repository = $repository AND pull_request = $pullRequest AND head_sha = $head AND steps = $steps LIMIT 1;";
            Bind(command, approval);
            return command.ExecuteScalar() is not null;
        });
    }

    /// <summary>Adds all of them or none, without a trace: the way the tests seed a store. The product records through the ledger.</summary>
    internal bool TryAddRange(IReadOnlyList<Approval> approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        if (approvals.Select(Key).Distinct().Count() != approvals.Count)
        {
            return false;
        }

        return SqliteDatabase.Guard(() => database.InWriteTransaction(connection =>
        {
            var added = HasRoom(connection, _capacity, approvals.Count) && InsertAll(connection, approvals);
            return (added, added);
        }));
    }

    /// <summary>Whether this many more fit in a queue of this capacity.</summary>
    internal static bool HasRoom(SqliteConnection connection, int capacity, int adding) => adding <= capacity - Count(connection);

    /// <summary>Inserts all of them in the transaction of <paramref name="connection"/>; false when the same decision is already there (nothing of the batch stays once the caller rolls back).</summary>
    internal static bool InsertAll(SqliteConnection connection, IReadOnlyList<Approval> approvals)
    {
        try
        {
            foreach (var approval in approvals)
            {
                Insert(connection, approval);
            }
            return true;
        }
        catch (SqliteException failure) when (SqliteDatabase.IsDuplicate(failure))
        {
            return false;
        }
    }

    public IReadOnlyList<Approval> List(int limit = int.MaxValue) => SqliteDatabase.Guard<IReadOnlyList<Approval>>(() =>
    {
        using var connection = database.Connect();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, work_item_id, repository, pull_request, head_sha, steps, approver, approved_at, step_up_at FROM approvals ORDER BY approved_at DESC, rowid DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var approvals = new List<Approval>();
        while (reader.Read())
        {
            approvals.Add(new Approval(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                [.. reader.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Enum.Parse<StepKind>)],
                reader.GetString(6),
                SqliteDatabase.Parse(reader.GetString(7)),
                reader.IsDBNull(8) ? null : SqliteDatabase.Parse(reader.GetString(8))));
        }
        return approvals;
    });

    internal static int Count(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM approvals;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static string Key(Approval approval) => $"{approval.Repository}#{approval.PullRequest}@{approval.HeadSha}:{Steps(approval)}";

    internal static void Insert(SqliteConnection connection, Approval approval)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO approvals (id, work_item_id, repository, pull_request, head_sha, steps, approver, approved_at, step_up_at) VALUES ($id, $workItem, $repository, $pullRequest, $head, $steps, $approver, $approvedAt, $stepUpAt);";
        Bind(command, approval);
        command.Parameters.AddWithValue("$id", approval.Id.ToString("D"));
        command.Parameters.AddWithValue("$workItem", approval.WorkItemId);
        command.Parameters.AddWithValue("$approver", approval.ApproverSubject);
        command.Parameters.AddWithValue("$approvedAt", SqliteDatabase.Format(approval.ApprovedAt));
        command.Parameters.AddWithValue("$stepUpAt", approval.StepUpAt is { } at ? SqliteDatabase.Format(at) : DBNull.Value);
        command.ExecuteNonQuery();
    }

    // What makes two approvals the same decision: this repository, this pull request, this commit, these steps
    private static void Bind(SqliteCommand command, Approval approval)
    {
        command.Parameters.AddWithValue("$repository", approval.Repository);
        command.Parameters.AddWithValue("$pullRequest", approval.PullRequest);
        command.Parameters.AddWithValue("$head", approval.HeadSha);
        command.Parameters.AddWithValue("$steps", Steps(approval));
    }

    private static string Steps(Approval approval) => string.Join(',', approval.Steps.Order());
}
