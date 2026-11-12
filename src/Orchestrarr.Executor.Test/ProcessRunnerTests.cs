namespace Orchestrarr.Executor.Test;

/// <summary>The class that really starts programs, stops them on a timeout or a cancellation and reads what they print: with real processes (sh is on every host that runs the executor).</summary>
public sealed class ProcessRunnerTests : IDisposable
{
    private readonly SystemProcessRunner _runner = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("orchestrarr-process-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private Task<ProcessResult> Sh(string script, TimeSpan? timeout = null, int maxOutput = 4000, Action<string>? onLine = null, CancellationToken cancellationToken = default) =>
        _runner.RunAsync("sh", ["-c", script], null, new Dictionary<string, string>(), timeout ?? TimeSpan.FromSeconds(20), maxOutput, cancellationToken, onLine);

    [Fact]
    public async Task What_a_program_prints_is_read_line_by_line_as_it_comes_and_kept_with_its_exit_code()
    {
        var lines = new List<string>();

        var result = await Sh("echo one; echo two >&2; echo three; exit 3", onLine: lines.Add, cancellationToken: Ct);

        result.ExitCode.Should().Be(3);
        result.TimedOut.Should().BeFalse();
        result.OutputTail.Should().Contain("one").And.Contain("two").And.Contain("three");
        lines.Should().Equal("one", "three");
    }

    [Fact]
    public async Task A_line_with_no_end_is_cut_where_it_is_read_so_it_cannot_fill_the_memory()
    {
        var lines = new List<string>();

        var result = await Sh("head -c 300000 /dev/zero | tr '\\0' a; echo; echo after", maxOutput: 1_000_000, onLine: lines.Add, cancellationToken: Ct);

        lines.Should().HaveCount(2);
        lines[0].Length.Should().BeLessThan(70_000);
        lines[0].Should().EndWith("[line cut]");
        lines[1].Should().Be("after");
        result.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task The_colours_of_a_terminal_are_taken_out_of_the_output_and_only_the_end_of_a_long_output_is_kept()
    {
        var coloured = await Sh("printf '\\033[32mgreen\\033[0m\\n'", cancellationToken: Ct);
        var verbose = await Sh("i=0; while [ $i -lt 300 ]; do echo line-$i; i=$((i+1)); done", maxOutput: 100, cancellationToken: Ct);

        coloured.OutputTail.Should().Be("green");
        verbose.OutputTail.Length.Should().BeLessThanOrEqualTo(100);
        verbose.OutputTail.Should().EndWith("line-299");
    }

    [Fact]
    public async Task A_program_that_does_not_exist_is_a_127_with_a_message_and_not_an_exception()
    {
        var result = await _runner.RunAsync("a-program-that-does-not-exist", [], null, new Dictionary<string, string>(), TimeSpan.FromSeconds(5), 1000, Ct);

        result.ExitCode.Should().Be(127);
        result.OutputTail.Should().Contain("not found");
    }

    [Fact]
    public async Task A_program_that_takes_too_long_is_stopped_with_the_process_tree_and_says_so()
    {
        var pidFile = Path.Combine(_folder, "pid");

        var result = await Sh($"echo $$ > {pidFile}; sleep 60", timeout: TimeSpan.FromSeconds(1), cancellationToken: Ct);

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(-1);
        await WaitUntilGone(int.Parse((await File.ReadAllTextAsync(pidFile, Ct)).Trim(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_cancellation_stops_the_process_and_throws_it_is_not_a_timeout()
    {
        var pidFile = Path.Combine(_folder, "pid");
        using var stop = new CancellationTokenSource();
        var run = Sh($"echo $$ > {pidFile}; sleep 60", cancellationToken: stop.Token);
        while (!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0)
        {
            await Task.Delay(20, Ct);
        }

        await stop.CancelAsync();

        await run.Awaiting(task => task).Should().ThrowAsync<OperationCanceledException>();
        await WaitUntilGone(int.Parse((await File.ReadAllTextAsync(pidFile, Ct)).Trim(), System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task WaitUntilGone(int pid)
    {
        for (var attempt = 0; attempt < 100 && Directory.Exists($"/proc/{pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}"); attempt++)
        {
            await Task.Delay(50, Ct);
        }
        Directory.Exists($"/proc/{pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}").Should().BeFalse("the process was killed");
    }
}
