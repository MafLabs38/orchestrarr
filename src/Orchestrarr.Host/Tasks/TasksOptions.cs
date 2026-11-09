using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Host.Storage;

namespace Orchestrarr.Host.Tasks;

/// <summary>A token that one executor presents. It is a secret: it comes from the environment or a secret file, never from a committed setting.</summary>
public sealed class ExecutorTokenOptions
{
    public string Name { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;
}

/// <summary>What the host reads about the tasks and the executors (ADR 0017).</summary>
public sealed partial class TasksOptions
{
    public const string SectionName = "Tasks";

    /// <summary>How long an approval waits for the executor, in minutes.</summary>
    public int BatchLifetimeMinutes { get; set; } = 60;

    /// <summary>How long a batch may stay with an executor that does not answer, in minutes.</summary>
    public int DeliveryTimeoutMinutes { get; set; } = 10;

    /// <summary>
    /// Whether approving needs the code of the second factor, which the executor checks. Unset: yes, except for the sandbox operator of
    /// <c>Approvals:Mode=Development</c>, which has no executor behind it.
    /// </summary>
    public bool? RequireSecondFactor { get; set; }

    /// <summary>
    /// Where the journal of approvals is written, for the double control of the executor. Unset: next to the database when there is one;
    /// with no file at all there is no journal, and an executor then refuses every batch.
    /// </summary>
    public string JournalPath { get; set; } = string.Empty;

    /// <summary>The executors that may ask for work, each with a token of its own. None: the routes of the executor are not there.</summary>
    public List<ExecutorTokenOptions> Executors { get; set; } = [];

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex NameShape();

    /// <summary>Checked when the host starts: a token that is too short, or two executors with one name, stops the host.</summary>
    public TasksSettings Validate(ApprovalsOptions approvals, IStorageSettings storage, string contentRoot)
    {
        if (BatchLifetimeMinutes is < 1 or > 1440 || DeliveryTimeoutMinutes is < 1 or > 120)
        {
            throw new InvalidOperationException("Tasks:BatchLifetimeMinutes must be 1 to 1440 and Tasks:DeliveryTimeoutMinutes 1 to 120.");
        }
        var credentials = new List<ExecutorCredential>();
        foreach (var executor in Executors)
        {
            if (!NameShape().IsMatch(executor.Name) || credentials.Exists(known => known.Name == executor.Name))
            {
                throw new InvalidOperationException("Tasks:Executors: each executor needs a distinct name (letters, digits and . _ -).");
            }
            if (executor.Token.Length < 32)
            {
                throw new InvalidOperationException($"Tasks:Executors:{executor.Name}: the token must be at least 32 characters (openssl rand -hex 32).");
            }
            credentials.Add(new ExecutorCredential(executor.Name, SHA256.HashData(Encoding.UTF8.GetBytes(executor.Token))));
        }

        return new TasksSettings(
            new TaskOptions
            {
                BatchLifetime = TimeSpan.FromMinutes(BatchLifetimeMinutes),
                DeliveryTimeout = TimeSpan.FromMinutes(DeliveryTimeoutMinutes),
                RequireSecondFactor = RequireSecondFactor ?? approvals.Mode != ApprovalsMode.Development,
            },
            JournalFile(storage, contentRoot),
            credentials);
    }

    // Where the journal is written: the path given, else next to the database, else nowhere
    private string? JournalFile(IStorageSettings storage, string contentRoot)
    {
        if (JournalPath.Length > 0)
        {
            return Path.GetFullPath(JournalPath, contentRoot);
        }
        return storage is SqliteStorage file ? Path.Combine(Path.GetDirectoryName(file.Path) ?? contentRoot, "approvals-journal.jsonl") : null;
    }
}

public sealed record ExecutorCredential(string Name, byte[] TokenHash);

/// <summary>The settings once checked. <c>JournalPath</c> is null when there is nowhere to write one.</summary>
public sealed record TasksSettings(TaskOptions Options, string? JournalPath, IReadOnlyList<ExecutorCredential> Executors)
{
    /// <summary>What the tests and a bare registration get: no code required, no journal, no executor.</summary>
    public static TasksSettings Bare { get; } = new(new TaskOptions { RequireSecondFactor = false }, null, []);
}
