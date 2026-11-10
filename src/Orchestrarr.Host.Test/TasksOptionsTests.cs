using Orchestrarr.Host.Approvals;
using Orchestrarr.Host.Tasks;
using Orchestrarr.Host.Storage;

namespace Orchestrarr.Host.Test;

/// <summary>A wrong <c>Tasks</c> setting stops the host at its start (ADR 0017).</summary>
public sealed class TasksOptionsTests
{
    private static readonly string Token = new('a', 40);

    private static TasksSettings Validate(TasksOptions options) =>
        options.Validate(new ApprovalsOptions(), new StorageOptions { Provider = StorageProvider.Memory }.Validate(Path.GetTempPath(), development: true, approving: true), Path.GetTempPath());

    [Fact]
    public void One_executor_with_a_long_enough_token_is_accepted()
    {
        var settings = Validate(new TasksOptions { Executors = [new ExecutorTokenOptions { Name = "executor-1", Token = Token }] });

        settings.Executors.Should().ContainSingle().Which.Name.Should().Be("executor-1");
    }

    [Fact]
    public void Several_executors_are_refused_until_batches_are_routed()
    {
        var options = new TasksOptions
        {
            Executors = [new ExecutorTokenOptions { Name = "executor-1", Token = Token }, new ExecutorTokenOptions { Name = "executor-2", Token = Token }],
        };

        options.Invoking(Validate).Should().Throw<InvalidOperationException>().WithMessage("*one executor*");
    }

    [Fact]
    public void A_short_token_stops_the_host()
    {
        var options = new TasksOptions { Executors = [new ExecutorTokenOptions { Name = "executor-1", Token = "short" }] };

        options.Invoking(Validate).Should().Throw<InvalidOperationException>().WithMessage("*32 characters*");
    }
}
