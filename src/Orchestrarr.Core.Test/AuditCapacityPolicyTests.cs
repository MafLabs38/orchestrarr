using Orchestrarr.Core.Approvals;

namespace Orchestrarr.Core.Test;

public sealed class AuditCapacityPolicyTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    [InlineData(8, 6)]
    [InlineData(2000, 1500)]
    [InlineData(int.MaxValue, 1_610_612_735)]
    public void Refusals_may_use_three_quarters_of_the_trail_rounded_down(int capacity, int limit)
    {
        AuditCapacityPolicy.RefusalLimit(capacity).Should().Be(limit);
    }

    [Theory]
    [InlineData(8, 0, 0, 8, 0, true)]
    [InlineData(8, 0, 0, 9, 0, false)]
    [InlineData(8, 8, 0, 1, 0, false)]
    [InlineData(8, 8, 0, 0, 0, true)]
    [InlineData(8, 3, 3, 3, 3, true)]
    [InlineData(8, 3, 3, 4, 4, false)]
    [InlineData(8, 0, 6, 1, 1, false)]
    [InlineData(8, 0, 6, 1, 0, true)]
    [InlineData(1, 0, 0, 1, 1, false)]
    [InlineData(1, 0, 0, 1, 0, true)]
    public void A_batch_fits_when_there_is_room_and_its_refusals_stay_in_their_share(int capacity, int held, int refusalsHeld, int adding, int refusalsAdding, bool fits)
    {
        AuditCapacityPolicy.Fits(capacity, held, refusalsHeld, adding, refusalsAdding).Should().Be(fits);
    }
}
