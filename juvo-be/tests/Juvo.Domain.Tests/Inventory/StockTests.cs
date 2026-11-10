using Juvo.Domain.Inventory;

namespace Juvo.Domain.Tests.Inventory;

public class StockLevelTests
{
    private static StockLevel Level(int onHand = 0)
    {
        var level = new StockLevel(Guid.NewGuid(), Guid.NewGuid());
        if (onHand > 0)
        {
            Assert.True(level.TryChangeOnHand(onHand));
        }

        return level;
    }

    [Fact]
    public void Available_is_on_hand_minus_reserved()
    {
        var level = Level(10);
        Assert.True(level.TryReserve(4));

        Assert.Equal((10, 4, 6), (level.OnHand, level.Reserved, level.Available));
    }

    [Fact]
    public void Stock_never_goes_below_zero_or_below_what_is_reserved()
    {
        var level = Level(5);
        Assert.True(level.TryReserve(3));

        Assert.False(level.TryChangeOnHand(-3)); // would leave 2 on hand but 3 reserved
        Assert.True(level.TryChangeOnHand(-2));  // leaves exactly the reserved units
        Assert.False(level.TryChangeOnHand(-1));
        Assert.Equal(3, level.OnHand);
    }

    [Fact]
    public void The_last_unit_can_only_be_reserved_once()
    {
        var level = Level(1);

        Assert.True(level.TryReserve(1));
        Assert.False(level.TryReserve(1));
        Assert.Equal(0, level.Available);
    }

    [Fact]
    public void Release_returns_reserved_units_and_cannot_release_more_than_reserved()
    {
        var level = Level(5);
        Assert.True(level.TryReserve(2));

        Assert.False(level.TryRelease(3));
        Assert.True(level.TryRelease(2));
        Assert.Equal((5, 0), (level.OnHand, level.Reserved));
    }

    [Fact]
    public void Deduct_at_packing_lowers_on_hand_and_reserved_together()
    {
        var level = Level(5);
        Assert.True(level.TryReserve(2));

        Assert.True(level.TryDeduct(2));
        Assert.Equal((3, 0, 3), (level.OnHand, level.Reserved, level.Available));
        Assert.False(level.TryDeduct(1)); // nothing reserved any more
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Reserve_release_and_deduct_need_a_positive_quantity(int quantity)
    {
        var level = Level(5);
        Assert.True(level.TryReserve(1));

        Assert.False(level.TryReserve(quantity));
        Assert.False(level.TryRelease(quantity));
        Assert.False(level.TryDeduct(quantity));
    }

    [Fact]
    public void Low_stock_is_flagged_only_when_a_threshold_is_set_and_available_reaches_it()
    {
        var level = Level(10);
        Assert.False(level.IsLow); // no threshold, no alert

        level.SetReorderLevel(3);
        Assert.False(level.IsLow);
        Assert.True(level.TryReserve(7));
        Assert.True(level.IsLow); // available 3 <= 3

        level.SetReorderLevel(-5);
        Assert.Equal(0, level.ReorderLevel);
        Assert.False(level.IsLow);
    }

    [Fact]
    public void On_hand_cannot_overflow()
    {
        var level = Level(int.MaxValue);

        Assert.False(level.TryChangeOnHand(1));
    }
}

public class StockTransferTests
{
    private static StockTransfer New() =>
        new(Guid.NewGuid(), Guid.NewGuid(), " urgent ", id => $"TR-{id:N}"[..9]);

    [Fact]
    public void A_transfer_needs_two_different_branches()
    {
        var branch = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new StockTransfer(branch, branch, null, _ => "TR-1"));
    }

    [Fact]
    public void Follows_requested_approved_in_transit_received_and_records_who_and_when()
    {
        var transfer = New();
        var user = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(TransferStatus.Requested, transfer.Status);
        Assert.Equal("urgent", transfer.Note);

        transfer.Approve(now, user);
        transfer.Ship(now.AddHours(1), user);
        transfer.Receive(now.AddDays(1), user);

        Assert.Equal(TransferStatus.Received, transfer.Status);
        Assert.Equal((now, user), (transfer.ApprovedAt, transfer.ApprovedBy));
        Assert.NotNull(transfer.ShippedAt);
        Assert.NotNull(transfer.ClosedAt);
    }

    [Fact]
    public void Steps_cannot_be_skipped_or_repeated()
    {
        var transfer = New();

        Assert.Throws<InvalidOperationException>(() => transfer.Ship(DateTimeOffset.UtcNow, null));
        Assert.Throws<InvalidOperationException>(() => transfer.Receive(DateTimeOffset.UtcNow, null));
        transfer.Approve(DateTimeOffset.UtcNow, null);
        Assert.Throws<InvalidOperationException>(() => transfer.Approve(DateTimeOffset.UtcNow, null));
        Assert.Throws<InvalidOperationException>(() => transfer.Reject(DateTimeOffset.UtcNow, null, "no"));
    }

    [Fact]
    public void A_received_or_rejected_transfer_cannot_be_cancelled_but_an_open_one_can()
    {
        var received = New();
        received.Approve(DateTimeOffset.UtcNow, null);
        received.Ship(DateTimeOffset.UtcNow, null);
        received.Receive(DateTimeOffset.UtcNow, null);
        Assert.Throws<InvalidOperationException>(() => received.Cancel(DateTimeOffset.UtcNow, null, "x"));

        var rejected = New();
        rejected.Reject(DateTimeOffset.UtcNow, null, "no stock");
        Assert.Equal(TransferStatus.Rejected, rejected.Status);
        Assert.Throws<InvalidOperationException>(() => rejected.Cancel(DateTimeOffset.UtcNow, null, "x"));

        var open = New();
        open.Approve(DateTimeOffset.UtcNow, null);
        open.Ship(DateTimeOffset.UtcNow, null);
        open.Cancel(DateTimeOffset.UtcNow, null, " changed plan ");
        Assert.Equal((TransferStatus.Cancelled, "changed plan"), (open.Status, open.CancelReason));
    }

    [Fact]
    public void The_same_variant_twice_adds_up_and_quantities_must_be_positive()
    {
        var transfer = New();
        var variant = Guid.NewGuid();

        transfer.AddLine(variant, 2);
        transfer.AddLine(variant, 3);

        Assert.Equal(5, Assert.Single(transfer.Lines).Quantity);
        Assert.Throws<ArgumentOutOfRangeException>(() => transfer.AddLine(Guid.NewGuid(), 0));
    }
}

public class StockCountTests
{
    private static StockCount New() => new(Guid.NewGuid(), null, _ => "SC-1");

    [Fact]
    public void Difference_is_counted_minus_the_snapshot_and_empty_until_counted()
    {
        var count = New();
        count.AddLine(Guid.NewGuid(), 10);
        var line = count.Lines[0];

        Assert.Null(line.Difference);
        line.SetCounted(7);
        Assert.Equal(-3, line.Difference);
        line.SetCounted(null);
        Assert.Null(line.Difference);
    }

    [Fact]
    public void Follows_in_progress_submitted_approved_and_can_be_reopened()
    {
        var count = New();
        count.Submit(DateTimeOffset.UtcNow, null);
        count.Reopen();
        Assert.Equal(CountStatus.InProgress, count.Status);

        count.Submit(DateTimeOffset.UtcNow, null);
        count.Approve(DateTimeOffset.UtcNow, null);

        Assert.Equal(CountStatus.Approved, count.Status);
        Assert.NotNull(count.ClosedAt);
        Assert.Throws<InvalidOperationException>(() => count.Cancel(DateTimeOffset.UtcNow, null));
        Assert.Throws<InvalidOperationException>(() => count.Submit(DateTimeOffset.UtcNow, null));
    }

    [Fact]
    public void Approval_needs_a_submitted_count()
    {
        var count = New();

        Assert.Throws<InvalidOperationException>(() => count.Approve(DateTimeOffset.UtcNow, null));
    }
}
