using Orchestrarr.Host.Missions;

namespace Orchestrarr.Host.Test;

public sealed class MissionActEndpointsTests
{
    [Theory]
    [InlineData("405: Repository rule violations found\n\nRequired status check \"build\" is queued.", true)]
    [InlineData("405: Repository rule violations found\n\nRequired status check \"build\" is in progress.", true)]
    [InlineData("405: Repository rule violations found\n\nRequired status check \"build\" is expected.", true)]
    [InlineData("405: Repository rule violations found\n\nRequired status check \"build\" has failed.", false)]
    [InlineData("409: Head branch was modified. Review and try the merge again.", false)]
    [InlineData(null, false)]
    public void A_merge_that_the_forge_refuses_because_a_required_check_is_not_over_is_asked_again(string? error, bool retry) =>
        MissionActEndpoints.ChecksNotOverYet(error).Should().Be(retry);
}
