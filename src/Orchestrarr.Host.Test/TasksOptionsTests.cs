using Microsoft.Extensions.Configuration;
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
    public void The_old_name_of_the_section_is_refused_instead_of_being_ignored()
    {
        var old = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Tasks:Executors:0:Name"] = "x", ["Tasks:Executors:0:Token"] = Token }).Build();
        var current = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Tasks:Runners:0:Name"] = "x" }).Build();

        TasksOptions.RenamedSettingsRefusal(old).Should().Contain("Tasks:Runners");
        TasksOptions.RenamedSettingsRefusal(current).Should().BeNull();
    }

    [Fact]
    public void One_runner_with_a_long_enough_token_is_accepted()
    {
        var settings = Validate(new TasksOptions { Runners = [new RunnerTokenOptions { Name = "runner-1", Token = Token }] });

        settings.Runners.Should().ContainSingle().Which.Name.Should().Be("runner-1");
    }

    [Fact]
    public void Several_runners_are_refused_until_batches_are_routed()
    {
        var options = new TasksOptions
        {
            Runners = [new RunnerTokenOptions { Name = "runner-1", Token = Token }, new RunnerTokenOptions { Name = "runner-2", Token = Token }],
        };

        options.Invoking(Validate).Should().Throw<InvalidOperationException>().WithMessage("*one runner*");
    }

    [Fact]
    public void A_short_token_stops_the_host()
    {
        var options = new TasksOptions { Runners = [new RunnerTokenOptions { Name = "runner-1", Token = "short" }] };

        options.Invoking(Validate).Should().Throw<InvalidOperationException>().WithMessage("*32 characters*");
    }
}
