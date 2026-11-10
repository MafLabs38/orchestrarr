using Microsoft.Extensions.DependencyInjection;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Core.Tasks;
using Orchestrarr.Host.Storage;
using Orchestrarr.Host.Tasks;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Test;

/// <summary>The storage is opened when the host starts, by a step that belongs to its registration.</summary>
public sealed class SqliteStartupTests : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private readonly string _folder = Directory.CreateTempSubdirectory("orchestrarr-startup-").FullName;

    private string DatabasePath => Path.Combine(_folder, "orchestrarr.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a test that failed: the temp folder is cleaned by the system
        }
    }

    private static ServiceProvider ProviderFor(string path)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(provider => SqliteDatabase.OpenAsSoleWriter(path, startEngagedWhenNew: true, provider.GetRequiredService<TimeProvider>()));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Starting_opens_the_database_and_holds_the_lock_for_the_life_of_the_host()
    {
        await using var provider = ProviderFor(DatabasePath);

        await new SqliteStartup(provider).StartAsync(Ct);

        File.Exists(DatabasePath).Should().BeTrue();
        var another = () => SqliteWriterLock.Acquire(DatabasePath);
        another.Should().Throw<WriterLockException>("the host holds the lock as long as it lives");
    }

    [Fact]
    public async Task Starting_with_the_lock_held_by_another_host_fails_with_the_lock_and_touches_nothing()
    {
        using var held = SqliteWriterLock.Acquire(DatabasePath);
        await using var provider = ProviderFor(DatabasePath);

        var start = async () => await new SqliteStartup(provider).StartAsync(Ct);

        await start.Should().ThrowAsync<WriterLockException>();
        File.Exists(DatabasePath).Should().BeFalse("the lock comes before the database is created, migrated or written");
    }

    [Fact]
    public async Task Starting_with_a_file_that_is_not_a_database_fails_at_start()
    {
        await File.WriteAllTextAsync(DatabasePath, "this is not a database", Ct);
        await using var provider = ProviderFor(DatabasePath);

        var start = async () => await new SqliteStartup(provider).StartAsync(Ct);

        await start.Should().ThrowAsync<Exception>().Where(failure => failure.ToString().Contains("not a database", StringComparison.Ordinal));
    }

    [Fact]
    public void The_registration_of_the_file_brings_its_own_startup_step()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);

        services.AddApprovals(new ApprovalsOptions(), [], new SqliteStorage(DatabasePath, StartEngagedWhenNew: true), new TasksSettings(new TaskOptions { RequireSecondFactor = false }, null, []));

        services.Should().Contain(descriptor => descriptor.ImplementationType == typeof(SqliteStartup), "no entry point can forget to open the file");
    }
}
