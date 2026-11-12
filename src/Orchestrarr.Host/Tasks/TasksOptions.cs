using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Host.Storage;

namespace Orchestrarr.Host.Tasks;

/// <summary>A token that one runner presents. It is a secret: it comes from the environment or a secret file, never from a committed setting.</summary>
public sealed class RunnerTokenOptions
{
    public string Name { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;
}

/// <summary>What the host reads about the tasks and the runners (ADR 0017).</summary>
public sealed partial class TasksOptions
{
    public const string SectionName = "Tasks";

    /// <summary>
    /// How long an approval waits for the runner, in minutes. The runner checks the code of the authenticator when it takes the batch, and a
    /// code is good for about a minute and a half: a batch that waits longer could only be refused for its code, so it expires instead, and the
    /// operator approves again with a fresh one.
    /// </summary>
    public int BatchLifetimeMinutes { get; set; } = 2;

    /// <summary>How long a batch may stay with a runner that goes silent, in minutes: every line a run sends starts the count again, and a quiet step of a validation can last ten.</summary>
    public int DeliveryTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Whether approving needs the code of the second factor, which the runner checks. Unset: yes. Only the tests of the control plane
    /// alone, which have no runner behind them, turn it off.
    /// </summary>
    public bool? RequireSecondFactor { get; set; }

    /// <summary>
    /// Where the journal of approvals is written, for the double control of the runner. Unset: in a `journal` folder next to the database when there is one;
    /// with no file at all there is no journal, and a runner then refuses every batch.
    /// </summary>
    public string JournalPath { get; set; } = string.Empty;

    /// <summary>The runners that may ask for work, each with a token of its own. None: the routes of the runner are not there.</summary>
    public List<RunnerTokenOptions> Runners { get; set; } = [];

    /// <summary>
    /// A reason when the configuration still uses the names from before the Orchestrarr Runner (ADR 0026): <c>Tasks:Executors</c> would be ignored, so there would be no
    /// Runner allowed to take a batch and nothing would say why. Null when the configuration is fine.
    /// </summary>
    public static string? RenamedSettingsRefusal(Microsoft.Extensions.Configuration.IConfiguration configuration) =>
        configuration.GetSection("Tasks:Executors").Exists() ? "Tasks:Executors was renamed Tasks:Runners (the executor is the Orchestrarr Runner, ADR 0026): move the settings (Tasks__Runners__0__Name, Tasks__Runners__0__Token)." : null;

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex NameShape();

    /// <summary>Checked when the host starts: a token that is too short, or two runners with one name, stops the host.</summary>
    public TasksSettings Validate(ApprovalsOptions approvals, IStorageSettings storage, string contentRoot)
    {
        if (BatchLifetimeMinutes is < 1 or > 1440 || DeliveryTimeoutMinutes is < 1 or > 120)
        {
            throw new InvalidOperationException("Tasks:BatchLifetimeMinutes must be 1 to 1440 and Tasks:DeliveryTimeoutMinutes 1 to 120.");
        }
        // A batch goes to whichever runner asks first, and a refusal by a runner that is not the right one is final: routing by the rights an
        // runner announced comes with the second runner (ADR 0017), and until then a second one is refused rather than left to take batches blindly.
        if (Runners.Count > 1)
        {
            throw new InvalidOperationException("Tasks:Runners: one runner is supported for now; batches are not routed between several (ADR 0017).");
        }
        var credentials = new List<RunnerCredential>();
        foreach (var runner in Runners)
        {
            if (!NameShape().IsMatch(runner.Name) || credentials.Exists(known => known.Name == runner.Name))
            {
                throw new InvalidOperationException("Tasks:Runners: each runner needs a distinct name (letters, digits and . _ -).");
            }
            if (runner.Token.Length < 32)
            {
                throw new InvalidOperationException($"Tasks:Runners:{runner.Name}: the token must be at least 32 characters (openssl rand -hex 32).");
            }
            credentials.Add(new RunnerCredential(runner.Name, SHA256.HashData(Encoding.UTF8.GetBytes(runner.Token))));
        }

        return new TasksSettings(
            new TaskOptions
            {
                BatchLifetime = TimeSpan.FromMinutes(BatchLifetimeMinutes),
                DeliveryTimeout = TimeSpan.FromMinutes(DeliveryTimeoutMinutes),
                RequireSecondFactor = RequireSecondFactor ?? true,
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
        return storage is SqliteStorage file ? Path.Combine(Path.GetDirectoryName(file.Path) ?? contentRoot, "journal", "approvals-journal.jsonl") : null;
    }
}

public sealed record RunnerCredential(string Name, byte[] TokenHash);

/// <summary>The settings once checked. <c>JournalPath</c> is null when there is nowhere to write one.</summary>
public sealed record TasksSettings(TaskOptions Options, string? JournalPath, IReadOnlyList<RunnerCredential> Runners);
