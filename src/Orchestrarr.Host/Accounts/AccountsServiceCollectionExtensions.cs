using Microsoft.AspNetCore.Identity;
using Orchestrarr.Core.Accounts;
using Orchestrarr.Host.Approvals;
using Orchestrarr.Host.Storage;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Accounts;

internal static class AccountsServiceCollectionExtensions
{
    /// <summary>
    /// The accounts, their sessions and the services that sign people in (ADR 0018), in the SQLite file or, in the sandbox, in memory.
    /// Registered after the storage, whose database they share, so that they come up after it.
    /// </summary>
    public static IServiceCollection AddAccounts(this IServiceCollection services, AuthConfig auth, IStorageSettings storage)
    {
        if (storage is SqliteStorage)
        {
            services
                .AddSingleton<IAccountStore>(provider => new SqliteAccountStore(provider.GetRequiredService<SqliteDatabase>()))
                .AddSingleton<ISessionStore>(provider => new SqliteSessionStore(provider.GetRequiredService<SqliteDatabase>()));
        }
        else
        {
            services.AddSingleton<IAccountStore, InMemoryAccountStore>().AddSingleton<ISessionStore, InMemorySessionStore>();
        }

        return services
            .AddSingleton(auth)
            .AddSingleton(auth.Settings)
            .AddSingleton<IApproverAccessor, SessionApprover>()
            .AddSingleton<IAccountAudit, TrailAccountAudit>()
            .AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>()
            .AddSingleton<LoginThrottle>()
            .AddSingleton<AuditSampler>()
            .AddSingleton(provider => new SessionService(provider.GetRequiredService<ISessionStore>(), provider.GetRequiredService<TimeProvider>(), auth.Settings.SessionMax))
            .AddSingleton<SignInService>()
            .AddSingleton<AccountService>()
            .AddSingleton<AccountSeeder>()
            .AddHostedService<AccountsStartup>();
    }
}

/// <summary>Creates the first administrator, or resets a password, when the host starts: a setting that is wrong stops the host here.</summary>
internal sealed class AccountsStartup(AccountSeeder seeder) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        seeder.Run();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
