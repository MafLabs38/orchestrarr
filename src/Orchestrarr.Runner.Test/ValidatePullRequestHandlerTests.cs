using Orchestrarr.Core.Tasks;

namespace Orchestrarr.Runner.Test;

public sealed class ValidatePullRequestHandlerTests : IDisposable
{
    private const string Repository = "example/app";

    private readonly RunnerFixture _world = new();
    private readonly ScriptedProcesses _processes = new();
    private readonly string _work = Directory.CreateTempSubdirectory("orchestrarr-work-").FullName;
    private readonly string _tokenFile;

    public ValidatePullRequestHandlerTests()
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
        Workshop = true,
        WorkRoot = _work,
        GitTokenFile = _tokenFile,
        Profiles = [new ValidationProfile { Repositories = ["example/*"], Image = "node:22-alpine", Steps = steps.Length > 0 ? steps : [Install, Tests] }],
    };

    private static ValidationStep Install { get; } = new() { Name = "npm ci", Command = "npm ci", Network = true };

    private static ValidationStep Tests { get; } = new() { Name = "npm test", Command = "npm test" };

    private static TaskSpec Spec(string repository = Repository) =>
        new(Guid.NewGuid(), 1, TaskKind.ValidatePullRequest, repository, 7, RunnerFixture.Head, Guid.NewGuid());

    private RunTrace Trace() => new(Guid.NewGuid(), "0", _world.Time, _world.Store);

    private async Task<(TaskOutcome Outcome, RunTrace Trace)> Run(ValidationOptions options, TaskSpec? task = null)
    {
        var trace = Trace();
        var outcome = await new ValidatePullRequestHandler(options, _processes).RunAsync(task ?? Spec(), trace, Ct);
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
        var fetch = git.Single(call => call.Arguments.Contains("fetch"));
        fetch.Arguments.Should().Contain("refs/pull/7/head").And.Contain("https://github.com/example/app.git");
        fetch.Environment["GIT_CONFIG_KEY_0"].Should().Be("http.extraHeader");
        git.Where(call => call != fetch).Should().OnlyContain(call => !call.Environment.ContainsKey("GIT_CONFIG_KEY_0"), "only the fetch carries the token");
        git.Should().OnlyContain(call => call.Environment["GIT_TERMINAL_PROMPT"] == "0" && call.Environment["GIT_CONFIG_NOSYSTEM"] == "1" && call.Environment["GIT_CONFIG_GLOBAL"] == "/dev/null");
        git.SelectMany(call => call.Arguments).Should().NotContain(argument => argument.Contains("the-secret-token"));
        var calls = _processes.Calls.ToList();
        calls.IndexOf(git.Single(call => call.Arguments.Contains("rev-parse"))).Should().BeLessThan(calls.FindIndex(call => call.File == "docker"));
    }

    [Fact]
    public async Task Each_step_runs_in_a_container_with_no_capability_no_root_and_a_bounded_size_and_the_network_only_where_the_step_asks()
    {
        await Run(Options());

        var containers = _processes.Calls.Where(call => call.File == "docker").Select(call => call.Arguments).ToList();
        containers.Should().HaveCount(2).And.OnlyContain(args => args.Contains("--cap-drop") && args.Contains("ALL") && args.Contains("no-new-privileges") && args.Contains("1000:1000") && args.Contains("--pids-limit") && args.Contains("--read-only"));
        containers.Should().OnlyContain(args => args.Contains("node:22-alpine"));
        containers.Select(args => string.Join(' ', args)).Should().OnlyContain(line => line.Contains("--cpus 1 ") && line.Contains("--memory 1024m ") && line.Contains("--pids-limit 512"));
        containers[0].Should().NotContain("--network");
        containers[1].Should().ContainInOrder("--network", "none");
        containers[1].Should().ContainInOrder("sh", "-c", "npm test");
    }

    [Fact]
    public async Task Git_on_the_host_has_its_directory_outside_the_folder_a_container_sees_and_its_hooks_and_monitors_off()
    {
        await Run(Options());

        var git = _processes.Calls.Where(call => call.File == "git" && call.Arguments.Contains("--git-dir")).ToList();
        git.Should().NotBeEmpty();
        var mounted = _processes.Calls.Where(call => call.File == "docker").Select(call => call.MountedWork).Distinct().Single()!;
        git.Should().OnlyContain(call => !call.After("--git-dir")!.StartsWith(mounted, StringComparison.Ordinal) && call.After("--work-tree") == mounted);
        git.Should().OnlyContain(call => call.Arguments.Contains("core.hooksPath=/dev/null") && call.Arguments.Contains("core.fsmonitor=false") && call.Arguments.Contains("http.followRedirects=false"));
        _processes.Calls.Single(call => call.File == "git" && call.Arguments.Contains("init")).Arguments.Should().Contain("--separate-git-dir").And.Contain("--template=");
    }

    [Fact]
    public async Task A_container_sees_the_work_folder_and_nothing_else_of_the_run()
    {
        await Run(Options());

        var docker = _processes.Calls.Where(call => call.File == "docker").ToList();
        docker.Select(call => call.After("-v")).Distinct().Should().ContainSingle().Which.Should().EndWith("/work:/work");
        docker.Select(call => call.Arguments.Count(argument => argument == "-v")).Should().OnlyContain(count => count == 1);
    }

    [Fact]
    public async Task The_token_of_the_forge_is_masked_in_every_line_of_the_trace_even_when_a_command_prints_it()
    {
        var header = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x-access-token:the-secret-token"));
        var plainBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("the-secret-token"));
        _processes.Script(call => IsTestStep(call), new ProcessResult(1, false, $"leak: the-secret-token and {plainBase64} and a header {header}"));

        var (_, trace) = await Run(Options());

        trace.Events.Select(line => line.Detail).Should().NotContain(detail => detail != null && detail.Contains("the-secret-token"));
        trace.Events.Select(line => line.Detail).Should().NotContain(detail => detail != null && detail.Contains(plainBase64));
        trace.Events.Should().Contain(line => line.Detail!.Contains("leak: *** and *** and a header"));
    }

    private static bool IsTestStep(Call call) => call.File == "docker" && call.Arguments.Contains("npm test");

    [Fact]
    public async Task The_container_gets_no_environment_from_the_runner_and_the_trace_never_holds_the_token()
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

    [Fact]
    public async Task A_container_that_cannot_be_stopped_refuses_the_task_instead_of_going_on_beside_it()
    {
        _processes.Script(call => call.Arguments.Contains("npm test"), new ProcessResult(-1, true, "still running"));
        _processes.Script(call => call.File == "docker" && call.Arguments[0] == "rm", new ProcessResult(1, false, "Error response from daemon: cannot kill container"));

        var (outcome, _) = await Run(Options());

        outcome.Result.Should().Be(TaskResult.Refused);
        outcome.Reason.Should().Be("a container could not be stopped");
    }

    [Theory]
    [InlineData(125, false)]
    [InlineData(127, true)]
    public async Task A_container_that_cannot_start_is_a_refusal_not_a_failed_validation(int exitCode, bool notStarted)
    {
        _processes.Script(call => call.File == "docker", new ProcessResult(exitCode, false, "Unable to find image", notStarted));

        var (outcome, _) = await Run(Options());

        outcome.Result.Should().Be(TaskResult.Refused);
        outcome.Reason.Should().Be("the container could not be started");
    }

    [Theory]
    [InlineData(126)]
    [InlineData(127)]
    public async Task A_step_whose_command_is_missing_or_not_executable_in_the_image_has_failed_it_did_not_fail_to_start(int exitCode)
    {
        _processes.Script(call => call.File == "docker", new ProcessResult(exitCode, false, "sh: 1: npm: not found"));

        var (outcome, _) = await Run(Options());

        outcome.Result.Should().Be(TaskResult.Failed);
        outcome.Reason.Should().Be($"npm ci failed (exit {exitCode})");
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
        var handlers = new ITaskHandler[] { new ValidatePullRequestHandler(Options(), _processes), new DryRunTaskHandler(TaskKind.MergePullRequest) };
        var batch = await _world.Approved(RunnerFixture.Batch(Repository));

        var result = await _world.Pipeline(handlers).RunAsync(new BatchDelivery(batch, _world.CodeAt()), () => false, Ct);

        result.Outcome.Final.Should().Be(BatchState.Done);
        result.Outcome.Tasks.Select(task => task.Result).Should().Equal(TaskResult.Failed, TaskResult.Skipped);
    }

    [Fact]
    public void A_validation_changes_nothing_outside_its_container_so_an_runner_that_only_validates_may_run_without_the_code()
    {
        var handlers = new ITaskHandler[] { new ValidatePullRequestHandler(Options(), _processes), new DryRunTaskHandler(TaskKind.MergePullRequest) };

        var pipeline = _world.Pipeline(handlers, requireSecondFactor: false);

        pipeline.SecondFactorRequired.Should().BeFalse();
    }

    [Fact]
    public void A_runner_that_writes_makes_the_runner_ask_for_the_code_of_the_second_factor()
    {
        var handlers = new ITaskHandler[] { new ValidatePullRequestHandler(Options(), _processes), new WritingRunner() };

        var withoutCode = () => _world.Pipeline(handlers, requireSecondFactor: false);

        withoutCode.Should().Throw<InvalidOperationException>();
    }

    private sealed class WritingRunner : ITaskHandler
    {
        public TaskKind Kind => TaskKind.MergePullRequest;

        public ValueTask<TaskOutcome> RunAsync(TaskSpec task, RunTrace trace, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    [Fact]
    public async Task A_stop_while_a_step_runs_kills_its_container_by_name_and_the_folders_are_removed()
    {
        _processes.Hang(IsTestStep);
        using var stop = new CancellationTokenSource();
        var run = new ValidatePullRequestHandler(Options(), _processes).RunAsync(Spec(), Trace(), stop.Token).AsTask();
        await _processes.WaitForCallAsync(IsTestStep, Ct);

        await stop.CancelAsync();

        await run.Awaiting(task => task).Should().ThrowAsync<OperationCanceledException>();
        _processes.Calls.Should().Contain(call => call.File == "docker" && call.Arguments[0] == "kill" && call.Arguments[1].StartsWith("orchestrarr-", StringComparison.Ordinal));
        Directory.GetDirectories(_work).Should().BeEmpty();
    }

}
