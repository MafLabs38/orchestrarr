using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host.Storage;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Approvals;

internal static class ApprovalsServiceCollectionExtensions
{
    /// <summary>
    /// Approving a decision card: the grants, the queue, the trail, the global stop and the service, in a SQLite file or in memory. The same
    /// instance answers to each interface it implements, so that the routes and the service see one trail and one stop.
    /// </summary>
    public static IServiceCollection AddApprovals(this IServiceCollection services, ApprovalsOptions options, IReadOnlyList<PowerGrant> grants, IStorageSettings storage)
    {
        // Signing in through the identity provider brings its own approver (AddSignIn, called before this): the host does not start without one
        if (options.Mode == ApprovalsMode.OpenIdConnect)
        {
            if (!services.Any(descriptor => descriptor.ServiceType == typeof(IApproverAccessor)))
            {
                throw new InvalidOperationException("Approvals:Mode=OpenIdConnect needs the sign-in: AddSignIn comes before AddApprovals.");
            }
        }
        else
        {
            services.AddSingleton<IApproverAccessor>(provider => options.Mode == ApprovalsMode.Development
                ? new DevelopmentApprover(options, provider.GetRequiredService<TimeProvider>())
                : new NoApprover());
        }

        services
            .AddSingleton(options)
            .AddSingleton(storage)
            .AddSingleton<IPowerPolicy>(new ConfiguredPowerPolicy(grants))
            .AddSingleton<IGrantedPowers, PolicyGrantedPowers>();

        // What must survive a restart is in one file, or, in the sandbox, in memory. The capacities default to what suits where it is kept.
        if (storage is SqliteStorage file)
        {
            var queueCapacity = options.QueueCapacity ?? StoreLimits.FileQueue;
            var auditCapacity = options.AuditCapacity ?? StoreLimits.FileAudit;
            services
                // Opened now, so that a file that cannot be used stops the host at its start and not at the first approval
                .AddSingleton(SqliteDatabase.Open(file.Path, file.StartEngagedWhenNew))
                .AddSingleton<IApprovalStore>(provider => new SqliteApprovalStore(provider.GetRequiredService<SqliteDatabase>(), queueCapacity))
                .AddSingleton<IApprovalLedger>(provider => new SqliteApprovalLedger(provider.GetRequiredService<SqliteDatabase>(), queueCapacity, auditCapacity, provider.GetService<ILogger<SqliteApprovalLedger>>()))
                .AddSingleton(provider => new SqliteKillSwitch(provider.GetRequiredService<SqliteDatabase>(), provider.GetService<ILogger<SqliteKillSwitch>>()))
                .AddSingleton<IKillSwitchControl>(provider => provider.GetRequiredService<SqliteKillSwitch>())
                .AddSingleton<IKillSwitch>(provider => provider.GetRequiredService<SqliteKillSwitch>())
                .AddSingleton(provider => new SqliteAuditLog(provider.GetRequiredService<SqliteDatabase>(), auditCapacity, provider.GetService<ILogger<SqliteAuditLog>>()))
                .AddSingleton<IAuditLog>(provider => provider.GetRequiredService<SqliteAuditLog>())
                .AddSingleton<IAuditSink>(provider => provider.GetRequiredService<SqliteAuditLog>());
        }
        else
        {
            var killSwitch = new InMemoryKillSwitch();
            var store = new InMemoryApprovalStore(options.QueueCapacity ?? StoreLimits.MemoryQueue);
            var auditLog = new InMemoryAuditLog(options.AuditCapacity ?? StoreLimits.MemoryAudit);
            services
                .AddSingleton<IApprovalStore>(store)
                .AddSingleton<IApprovalLedger>(provider => new InMemoryApprovalLedger(provider.GetRequiredService<IApprovalStore>(), provider.GetRequiredService<IAuditSink>()))
                .AddSingleton(killSwitch)
                .AddSingleton<IKillSwitchControl>(killSwitch)
                .AddSingleton<IKillSwitch>(killSwitch)
                .AddSingleton(auditLog)
                .AddSingleton<IAuditLog>(auditLog)
                .AddSingleton<IAuditSink>(auditLog);
        }

        return services.AddSingleton(provider => new ApprovalService(
            provider.GetRequiredService<WorkItemCatalog>(),
            provider.GetRequiredService<IApprovalStore>(),
            provider.GetRequiredService<IKillSwitch>(),
            provider.GetRequiredService<IAuditSink>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IApprovalLedger>()));
    }
}
