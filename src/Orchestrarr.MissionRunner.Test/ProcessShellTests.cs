using Orchestrarr.MissionRunner;

namespace Orchestrarr.MissionRunner.Test;

public sealed class ProcessShellTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_program_gives_back_its_exit_code_and_what_it_wrote()
    {
        var result = await new ProcessShell().RunAsync("sh", ["-c", "echo out; echo err >&2; exit 3"], TimeSpan.FromSeconds(10), Ct);

        result.ExitCode.Should().Be(3);
        result.Output.Should().Contain("out").And.Contain("err");
        result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task A_program_that_runs_too_long_is_killed_and_reported_as_timed_out()
    {
        var result = await new ProcessShell().RunAsync("sleep", ["30"], TimeSpan.FromMilliseconds(300), Ct);

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(-1);
    }
}
