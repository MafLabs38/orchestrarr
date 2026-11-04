using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Orchestrarr.Core.Launch;
using Orchestrarr.Host.Approvals;

namespace Orchestrarr.Host.Test;

/// <summary>The rate limit of approving is per person: whose turn it is decides who is held back.</summary>
public sealed class ApprovingPartitionTests
{
    private static DefaultHttpContext ContextOf(Approver? approver, string? address = null)
    {
        var services = new ServiceCollection().AddSingleton<IApproverAccessor>(new FixedApprover(approver)).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);
        return context;
    }

    [Fact]
    public void Two_people_have_two_turns_and_the_same_person_one_wherever_they_come_from()
    {
        var alice = new Approver("alice", IsPerson: true, IsOperator: true, StepUpAt: null);
        var bob = new Approver("bob", IsPerson: true, IsOperator: false, StepUpAt: null);

        ApprovalsServiceCollectionExtensions.PartitionOf(ContextOf(alice, "10.0.0.1")).Should().Be("alice");
        ApprovalsServiceCollectionExtensions.PartitionOf(ContextOf(alice, "10.0.0.2")).Should().Be("alice");
        ApprovalsServiceCollectionExtensions.PartitionOf(ContextOf(bob, "10.0.0.1")).Should().Be("bob");
    }

    [Fact]
    public void Nobody_signed_in_is_told_apart_by_address_and_never_shares_a_turn_with_a_person()
    {
        ApprovalsServiceCollectionExtensions.PartitionOf(ContextOf(null, "10.0.0.7")).Should().Be("10.0.0.7");
        ApprovalsServiceCollectionExtensions.PartitionOf(ContextOf(null)).Should().Be("unknown");
    }
}
