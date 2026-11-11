using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Executor.Test;

public sealed class ValidatePullRequestRunnerTests : IDisposable
{
    private const string Repository = "example/app";

    private readonly ExecutorFixture _world = new();
    private readonly ScriptedProcesses _processes = new();
    private readonly string _work = Directory.CreateTempSubdirectory("orchestrarr-work-").FullName;
    private readonly string _tokenFile;

    public ValidatePullRequestRunnerTests()
    {
        _tokenFile = Path.Combine(_work, "git-token");
        File.WriteAllText(_tokenFile, "the-secret-token\n");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _world.Dispose();
        if (Directory.Exists(_work))
        {
            Directory.Delete(_work, recursive: true);
        }
    }

    private ValidationOptions Options(params ValidationStep[] steps) => new()
    {
        WorkRoot = _work,
        GitTokenFile = _tokenFile,
        Profiles = [new ValidationProfile { Repositories = ["example/*"], Image = "node:22-alpine", Steps = steps.Length > 0 ? steps : [Install, Tests] }],
    };

    private static ValidationStep Install { get; } = new() { Name = "npm ci", Command = "npm ci", Network = true };

    private static ValidationStep Tests { get; } = new() { Name = "npm test", Command = "npm test" };

    private static TaskSpec Spec(string repository = Repository) =>
        new(Guid.NewGuid(), 1, TaskKind.ValidatePullRequest, repository, 7, ExecutorFixture.Head, Guid.NewGuid());

    private RunTrace Trace() => new(Guid.NewGuid(), "0", _world.Time, _world.Store);

    private async Task<(TaskOutcome Outcome, RunTrace Trace)> Run(ValidationOptions options, TaskSpec? task = null)
    {
        var trace = Trace();
        var outcome = await new ValidatePullRequestRunner(options, _processes).RunAsync(task ?? Spec(), trace, Ct);
        return (outcome, trace);
    }

    [Fact]
    public async Task A_pull_request_whose_steps_all_pass_is_passed_and_the_trace_tells_each_step()
    {
        var (outcome, trace) = await Run(Options());

        outcome.Result.Should().Be(TaskResult.Passed);
        outcome.Reason.Should().Be("2 steps passed");
        trace.Events.Select(line => line.Type).Should().Equal(RunEvents.TaskStarted, RunEvents.TaskStep, RunEvents.TaskStep, RunEvents.TaskStep, RunEvents.TaskPassed);
        trace.Events[1].Detail.Should().Contain("approved commit");
        trace.Events[2].Detail.Should().StartWith("npm ci: passed");
        trace.Events[3].Detail.Should().StartWith("npm test: passed");
    }

    [Fact]
    public async Task The_pull_request_is_fetched_with_the_token_in_the_environment_of_git_only_and_the_commit_is_checked_before_anything_runs()
    {
        await Run(Options());

        var git = _processes.Calls.Where(call => call.File == "git").ToList();
        git.Select(call => call.Arguments).Should().Contain(args => args.Contains("refs/pull/7/head") && args.Contains("https://github.com/example/app.git"));
        git.Should().OnlyContain(call => call.Environment["GIT_TERMINAL_PROMPT"] == "0" && call.Environment["GIT_CONFIG_KEY_0"] == "http.extraHeader");
        git.SelectMany(call => call.Arguments).Should().NotContain(argument => argument.Contains("the-secret-token"));
        _processes.Calls.IndexOf(git.Single(call => call.Arguments.Contains("rev-parse"))).Should().BeLessThan(_processes.Calls.FindIndex(call => call.File == "docker"));
    }

    [Fact]
    public async Task Each_step_runs_in_a_container_with_no_capability_no_root_and_a_bounded_size_and_the_network_only_where_the_step_asks()
    {
        await Run(Options());

        var containers = _processes.Calls.Where(call => call.File == "docker").Select(call => call.Arguments).ToList();
        containers.Should().HaveCount(2).And.OnlyContain(args => args.Contains("--cap-drop") && args.Contains("ALL") && args.Contains("no-new-privileges") && args.Contains("1000:1000") && args.Contains("--pids-limit"));
        containers.Should().OnlyContain(args => args.Contains("1") && args.Contains("1024m") && args.Contains("node:22-alpine"));
        containers[0].Should().NotContain("--network");
        containers[1].Should().ContainInOrder("--network", "none");
        containers[1].Should().ContainInOrder("sh", "-c", "npm test");
    }

    [Fact]
    public async Task The_container_gets_no_environment_from_the_executor_and_the_trace_never_holds_the_token()
    {
        var (_, trace) = await Run(Options());

        _processes.Calls.Where(call => call.File == "docker").Should().OnlyContain(call => call.Environment.Count == 0 && !call.Arguments.Any(argument => argument.Contains("the-secret-token")));
        trace.Events.Select(line => line.Detail).Should().NotContain(detail => detail != null && (detail.Contains("the-secret-token") || detail.Contains("Authorization")));
    }

    [Fact]
    public async Task A_line_longer_than_the_limit_is_cut_at_its_start_so_that_the_control_plane_never_refuses_the_trace()
    {
        _processes.Script(call => call.Arguments.Contains("npm test"), new ProcessResult(1, false, new string('x', 5000) + "THE END"));

        var (_, trace) = await Run(Options());

        var step = trace.Events.Single(line => line.Detail!.StartsWith("…", StringComparison.Ordinal));
        step.Detail!.Length.Should().BeLessThanOrEqualTo(TaskLimits.MaxEventDetail);
        step.Detail.Should().StartWith("…").And.EndWith("THE END");
    }

    [Fact]
    public async Task A_step_that_fails_is_a_result_and_the_next_steps_do_not_run()
    {
        _processes.Script(call => call.Arguments.Contains("npm ci"), new ProcessResult(1, false, "npm ERR! missing script"));

        var (outcome, trace) = await Run(Options());

        outcome.Result.Should().Be(TaskResult.Failed);
        outcome.Reason.Should().Be("npm ci failed (exit 1)");
        _processes.Calls.Count(call => call.File == "docker").Should().Be(1);
        trace.Events[^2].Detail.Should().Contain("npm ci: failed, exit 1").And.Contain("npm ERR! missing script");
        trace.Events[^1].Type.Should().Be(RunEvents.TaskFailed);
    }

    [Fact]
    public async Task A_step_that_takes_too_long_is_stopped_and_its_container_is_killed()
    {
        _processes.Script(call => call.Arguments.Contains("npm test"), new ProcessResult(-1, true, "still running"));

        var (outcome, _) = await Run(Options());

        outcome.Result.Should().Be(TaskResult.Failed);
        outcome.Reason.Should().Be("npm test took more than 10 min");
        _processes.Calls.Should().Contain(call => call.File == "docker" && call.Arguments[0] == "kill" && call.Arguments[1].StartsWith("orchestrarr-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(125)]
    [InlineData(126)]
    [InlineData(127)]
    public async Task A_container_that_cannot_start_is_a_refusal_not_a_failed_validation(int exitCode)
    {
        _processes.Script(call => call.File == "docker", new ProcessResult(exitCode, false, "Unable to find image"));

        var (outcome, _) = await Run(Options());

        outcome.Result.Should().Be(TaskResult.Refused);
        outcome.Reason.Should().Be("the container could not be started");
    }

    [Fact]
    public async Task A_pull_request_that_cannot_be_fetched_is_refused_and_nothing_runs()
    {
        _processes.Script(call => call.Arguments.Contains("fetch"), new ProcessResult(128, false, "Permission denied (publickey)"));

        var (outcome, trace) = await Run(Options());

        outcome.Result.Should().Be(TaskResult.Refused);
        _processes.Calls.Should().NotContain(call => call.File == "docker");
        trace.Events.Should().Contain(line => line.Type == RunEvents.TaskStep && line.Detail!.Contains("Permission denied"));
    }

    [Fact]
    public async Task A_head_that_moved_since_the_approval_is_refused_before_anything_runs()
    {
        _processes.Script(call => call.Arguments.Contains("rev-parse"), new ProcessResult(0, false, new string('b', 40)));

        var (outcome, trace) = await Run(Options());

        outcome.Result.Should().Be(TaskResult.Refused);
        outcome.Reason.Should().Be("the head of the pull request is not the approved commit");
        _processes.Calls.Should().NotContain(call => call.File == "docker");
        trace.Events.Should().Contain(line => line.Detail!.Contains("bbbbbbbb") && line.Detail.Contains("aaaaaaaa"));
    }

    [Fact]
    public async Task A_repository_with_no_profile_is_refused_and_nothing_is_started()
    {
        var (outcome, _) = await Run(Options(), Spec("other/app"));

        outcome.Result.Should().Be(TaskResult.Refused);
        outcome.Reason.Should().Be("no validation profile for this repository");
        _processes.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_folder_of_the_run_is_removed_whatever_the_result()
    {
        _processes.Script(call => call.Arguments.Contains("npm test"), new ProcessResult(1, false, "1 failing"));

        await Run(Options());

        Directory.GetDirectories(_work).Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_validation_stops_the_merge_of_the_same_approval_in_the_pipeline()
    {
        _processes.Script(call => call.Arguments.Contains("npm test"), new ProcessResult(1, false, "1 failing"));
        var runners = new ITaskRunner[] { new ValidatePullRequestRunner(Options(), _processes), new DryRunTaskRunner(TaskKind.MergePullRequest) };
        var batch = await _world.Approved(ExecutorFixture.Batch(Repository));

        var result = await _world.Pipeline(runners).RunAsync(new BatchDelivery(batch, _world.CodeAt()), () => false, Ct);

        result.Outcome.Final.Should().Be(BatchState.Done);
        result.Outcome.Tasks.Select(task => task.Result).Should().Equal(TaskResult.Failed, TaskResult.Skipped);
    }

    [Fact]
    public void A_real_validation_makes_the_executor_ask_for_the_code_of_the_second_factor()
    {
        var runners = new ITaskRunner[] { new ValidatePullRequestRunner(Options(), _processes), new DryRunTaskRunner(TaskKind.MergePullRequest) };

        var withoutCode = () => _world.Pipeline(runners, requireSecondFactor: false);

        withoutCode.Should().Throw<InvalidOperationException>();
    }

    private sealed record Call(string File, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment);

    private sealed class ScriptedProcesses : IProcessRunner
    {
        private readonly List<(Func<Call, bool> When, ProcessResult Result)> _scripts = [];

        public List<Call> Calls { get; } = [];

        public void Script(Func<Call, bool> when, ProcessResult result) => _scripts.Insert(0, (when, result));

        public Task<ProcessResult> RunAsync(string file, IReadOnlyList<string> arguments, string? workingDirectory, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, int maxOutputCharacters, CancellationToken cancellationToken)
        {
            var call = new Call(file, arguments, environment);
            Calls.Add(call);
            foreach (var (when, result) in _scripts)
            {
                if (when(call))
                {
                    return Task.FromResult(result);
                }
            }
            // By default: a fetch that works, at the approved commit, and a container that passes
            return Task.FromResult(arguments.Contains("rev-parse") ? new ProcessResult(0, false, ExecutorFixture.Head) : new ProcessResult(0, false, "ok"));
        }
    }
}
