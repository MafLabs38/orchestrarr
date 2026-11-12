using Microsoft.Extensions.Configuration;

namespace Orchestrarr.Runner.Test;

public sealed class RunnerProgramTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("orchestrarr-runner-store-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_old_names_of_the_settings_are_refused_instead_of_being_ignored()
    {
        var old = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Executor:Name"] = "x" }).Build();
        var current = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Runner:Name"] = "x" }).Build();

        RunnerProgram.RenamedSettingsRefusal(old, oldFileExists: false).Should().Contain("ORCHESTRARR_Runner__");
        RunnerProgram.RenamedSettingsRefusal(current, oldFileExists: true).Should().Contain("executor.json");
        RunnerProgram.RenamedSettingsRefusal(current, oldFileExists: false).Should().BeNull();
    }

    [Fact]
    public void The_store_of_the_executor_is_moved_with_its_companions_when_only_the_old_file_exists()
    {
        File.WriteAllText(Path.Combine(_folder, "executor.db"), "main");
        File.WriteAllText(Path.Combine(_folder, "executor.db-wal"), "wal");

        RunnerProgram.MoveOldStore(Path.Combine(_folder, "runner.db"));

        File.ReadAllText(Path.Combine(_folder, "runner.db")).Should().Be("main");
        File.ReadAllText(Path.Combine(_folder, "runner.db-wal")).Should().Be("wal");
        File.Exists(Path.Combine(_folder, "executor.db")).Should().BeFalse();
    }

    [Fact]
    public void A_store_that_already_has_the_new_name_or_a_path_set_by_hand_is_left_alone()
    {
        File.WriteAllText(Path.Combine(_folder, "executor.db"), "old");
        File.WriteAllText(Path.Combine(_folder, "runner.db"), "new");

        RunnerProgram.MoveOldStore(Path.Combine(_folder, "runner.db"));
        RunnerProgram.MoveOldStore(Path.Combine(_folder, "mine.db"));

        File.ReadAllText(Path.Combine(_folder, "runner.db")).Should().Be("new");
        File.ReadAllText(Path.Combine(_folder, "executor.db")).Should().Be("old");
    }
}
