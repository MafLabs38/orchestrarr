using Orchestrarr.Storage;

namespace Orchestrarr.Host.Storage;

/// <summary>
/// Opens the database when the host starts, not at the first approval: a file that cannot be used, or a second host on the same file, stops the
/// host here, before it listens. It belongs to the registration of the storage, so no entry point can forget it.
/// </summary>
internal sealed class SqliteStartup(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        services.GetRequiredService<SqliteDatabase>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
