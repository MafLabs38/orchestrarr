using System.Threading.RateLimiting;
using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host.Storage;
using Orchestrarr.Storage;

namespace Orchestrarr.Host.Approvals;

internal static class ApprovalsServiceCollectionExtensions
{
    /// <summary>The name of the rate limit on approving.</summary>
    internal const string ApprovingPolicy = "approving";

    // Whose turn it is: the person who signed in, else where the request comes from
    internal static string PartitionOf(HttpContext context) =>
        context.RequestServices.GetRequiredService<IApproverAccessor>().Current(context)?.Subject ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

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
            .AddApprovingRateLimit(options)
            .AddSingleton(options)
            .AddSingleton(storage)
            .AddSingleton<IPowerPolicy>(new ConfiguredPowerPolicy(grants))
            .AddSingleton<IGrantedPowers, PolicyGrantedPowers>();

        // What must survive a restart is in one file, or, in the sandbox, in memory. The capacities default to what suits where it is kept.
        var capacities = storage.Persistent
            ? new Capacities(options.QueueCapacity ?? StoreLimits.FileQueue, options.AuditCapacity ?? StoreLimits.FileAudit)
            : new Capacities(options.QueueCapacity ?? StoreLimits.MemoryQueue, options.AuditCapacity ?? StoreLimits.MemoryAudit);
        if (storage is SqliteStorage file)
        {
            services.AddSqliteStorage(file, capacities);
        }
        else
        {
            services.AddMemoryStorage(storage.StartsEngaged, capacities);
        }

        return services.AddSingleton(provider => new ApprovalService(
            provider.GetRequiredService<WorkItemCatalog>(),
            provider.GetRequiredService<IApprovalStore>(),
            provider.GetRequiredService<IKillSwitch>(),
            provider.GetRequiredService<IAuditSink>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IApprovalLedger>()));
    }

    private static IServiceCollection AddApprovingRateLimit(this IServiceCollection services, ApprovalsOptions options) =>
        services.AddRateLimiter(limits =>
        {
            limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limits.OnRejected = (rejected, token) => new ValueTask(rejected.HttpContext.Response.WriteAsJsonAsync(new ErrorResponse("tooManyRequests"), token));
            limits.AddPolicy(ApprovingPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                PartitionOf(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.ApprovalsLimit, Window = TimeSpan.FromMinutes(options.ApprovalsWindowMinutes), QueueLimit = 0 }));
        });

    // The stop, the trail and the queue each answer to every interface they implement: one instance, so that the routes and the service see one of each
    private static void AddSqliteStorage(this IServiceCollection services, SqliteStorage file, Capacities capacities)
    {
        var (queueCapacity, auditCapacity) = capacities;
        services
            // Opened when the host starts (Program asks for it), so that a file that cannot be used stops it there and not at the first approval
            // The one writer first: a host that is refused has not touched the file
            .AddSingleton(_ => SqliteWriterLock.Acquire(file.Path))
            .AddSingleton(provider =>
            {
                provider.GetRequiredService<SqliteWriterLock>();
                return SqliteDatabase.Open(file.Path, file.StartEngagedWhenNew, provider.GetRequiredService<TimeProvider>());
            })
            .AddSingleton<IApprovalStore>(provider => new SqliteApprovalStore(provider.GetRequiredService<SqliteDatabase>(), queueCapacity))
            .AddSingleton<IApprovalLedger>(provider => new SqliteApprovalLedger(provider.GetRequiredService<SqliteDatabase>(), provider.GetRequiredService<IKillSwitchControl>(), queueCapacity, auditCapacity, provider.GetService<ILogger<SqliteApprovalLedger>>()))
            .AddSingleton(provider => new SqliteKillSwitch(provider.GetRequiredService<SqliteDatabase>(), auditCapacity, provider.GetService<ILogger<SqliteKillSwitch>>()))
            .AddSingleton<IKillSwitchControl>(provider => provider.GetRequiredService<SqliteKillSwitch>())
            .AddSingleton<IKillSwitch>(provider => provider.GetRequiredService<SqliteKillSwitch>())
            .AddSingleton(provider => new SqliteAuditLog(provider.GetRequiredService<SqliteDatabase>(), auditCapacity, provider.GetService<ILogger<SqliteAuditLog>>()))
            .AddSingleton<IAuditLog>(provider => provider.GetRequiredService<SqliteAuditLog>())
            .AddSingleton<IAuditSink>(provider => provider.GetRequiredService<SqliteAuditLog>());
    }

    private static void AddMemoryStorage(this IServiceCollection services, bool startEngaged, Capacities capacities) =>
        services
            .AddSingleton(_ => new InMemoryApprovalStore(capacities.Queue))
            .AddSingleton<IApprovalStore>(provider => provider.GetRequiredService<InMemoryApprovalStore>())
            .AddSingleton<IApprovalLedger>(provider => new InMemoryApprovalLedger(provider.GetRequiredService<InMemoryApprovalStore>(), provider.GetRequiredService<IAuditSink>(), provider.GetRequiredService<InMemoryKillSwitch>()))
            // A store that starts engaged is a new store, as far as anyone can tell: the trail says so, with the time of the host
            .AddSingleton(provider => new InMemoryAuditLog(
                capacities.Audit,
                startEngaged ? [AuditEvents.InstanceCreatedEntry(provider.GetRequiredService<TimeProvider>().GetUtcNow(), "store")] : []))
            .AddSingleton<IAuditLog>(provider => provider.GetRequiredService<InMemoryAuditLog>())
            .AddSingleton<IAuditSink>(provider => provider.GetRequiredService<InMemoryAuditLog>())
            .AddSingleton(provider => new InMemoryKillSwitch(provider.GetRequiredService<InMemoryAuditLog>(), startEngaged))
            .AddSingleton<IKillSwitchControl>(provider => provider.GetRequiredService<InMemoryKillSwitch>())
            .AddSingleton<IKillSwitch>(provider => provider.GetRequiredService<InMemoryKillSwitch>());

    // The two bounds, decided once for the kind of storage there is
    private readonly record struct Capacities(int Queue, int Audit);
}
