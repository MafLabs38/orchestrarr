using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.WorkItems;

namespace Orchestrarr.Host.Approvals;

internal static class ApprovalsServiceCollectionExtensions
{
    /// <summary>
    /// Approving a decision card: the grants, the queue, the trail, the global stop and the service, in memory. The same instance answers to
    /// each interface it implements, so that the routes and the service see one trail and one stop.
    /// </summary>
    public static IServiceCollection AddApprovals(this IServiceCollection services, ApprovalsOptions options, IReadOnlyList<PowerGrant> grants)
    {
        var killSwitch = new InMemoryKillSwitch();
        var auditLog = new InMemoryAuditLog(options.AuditCapacity);

        // Signing in through the identity provider brings its own approver (AddSignIn)
        if (options.Mode != ApprovalsMode.OpenIdConnect)
        {
            services.AddSingleton<IApproverAccessor>(provider => options.Mode == ApprovalsMode.Development
                ? new DevelopmentApprover(options, provider.GetRequiredService<TimeProvider>())
                : new NoApprover());
        }

        return services
            .AddSingleton(options)
            .AddSingleton<IPowerPolicy>(new ConfiguredPowerPolicy(grants))
            .AddSingleton<IGrantedPowers, PolicyGrantedPowers>()
            .AddSingleton<IApprovalStore>(new InMemoryApprovalStore(options.QueueCapacity))
            .AddSingleton(killSwitch)
            .AddSingleton<IKillSwitchControl>(killSwitch)
            .AddSingleton<IKillSwitch>(killSwitch)
            .AddSingleton(auditLog)
            .AddSingleton<IAuditLog>(auditLog)
            .AddSingleton<IAuditSink>(auditLog)
            .AddSingleton<ApprovalService>();
    }
}
