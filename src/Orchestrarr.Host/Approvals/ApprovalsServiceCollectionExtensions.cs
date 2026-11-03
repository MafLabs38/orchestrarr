using Orchestrarr.Core.Approvals;
using Orchestrarr.Core.Launch;
using Orchestrarr.Core.WorkItems;
using Orchestrarr.Host.SignIn;

namespace Orchestrarr.Host.Approvals;

internal static class ApprovalsServiceCollectionExtensions
{
    /// <summary>
    /// Approving a decision card: the grants, the queue, the trail, the global stop and the service, in memory. The same instance answers to
    /// each interface it implements, so that the routes and the service see one trail and one stop.
    /// </summary>
    public static IServiceCollection AddApprovals(this IServiceCollection services, ApprovalsOptions options, IReadOnlyList<PowerGrant> grants, SignInOptions? signIn = null)
    {
        var killSwitch = new InMemoryKillSwitch();
        var auditLog = new InMemoryAuditLog(options.AuditCapacity);

        return services
            .AddSingleton(options)
            .AddSingleton<IPowerPolicy>(new ConfiguredPowerPolicy(grants))
            .AddSingleton<IGrantedPowers, PolicyGrantedPowers>()
            .AddSingleton<IApproverAccessor>(provider => options.Mode switch
            {
                ApprovalsMode.Development => new DevelopmentApprover(options, provider.GetRequiredService<TimeProvider>()),
                ApprovalsMode.OpenIdConnect => new OidcApprover(signIn ?? throw new InvalidOperationException("SignIn is not configured.")),
                _ => new NoApprover(),
            })
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
