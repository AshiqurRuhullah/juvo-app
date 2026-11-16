using Juvo.Domain.Orders;

namespace Juvo.Domain.Tests.Orders;

/// <summary>Order line snapshots of module Z and the pre-order state of module AA.</summary>
public class OrderPromotionTests
{
    private static readonly ShippingSnapshot Shipping = new("Nadia", "+8801712345678", "BD", null, null, null, null, null, null, null, "House 1", null, null, null, null, null, null);

    private static Order Place(decimal promotionDiscount, params NewOrderItem[] items) =>
        Order.Place(new NewOrder("JV-HO-2611-000001", Guid.NewGuid(), OrderSource.Web, Guid.NewGuid(), "+8801712345678", "Nadia", null, "COD", "DefaultBranch", null, "BDT", items.Sum(i => i.UnitPrice * i.Quantity),
            50m, "SAVE", null, 0m, 60m, 60m, 0m, 500, Shipping, null, null, "fp", "hash", null, null, true, null, null, null, promotionDiscount), items, DateTimeOffset.UtcNow);

    [Fact]
    public void The_grand_total_takes_the_promotion_discount_the_coupon_and_the_manual_discount_off_the_goods()
    {
        var order = Place(100m, new NewOrderItem(Guid.NewGuid(), Guid.NewGuid(), "Tee", null, "TEE", null, null, null, 800m, 2, 100, true));
        Assert.Equal(1600m, order.Subtotal);
        Assert.Equal(100m, order.PromotionDiscount);
        Assert.Equal(1600m - 100m - 50m + 60m, order.GrandTotal);
    }

    [Fact]
    public void A_line_keeps_its_list_price_its_discount_share_and_its_adjustments()
    {
        var promotion = Guid.NewGuid();
        var order = Place(100m, new NewOrderItem(Guid.NewGuid(), Guid.NewGuid(), "Tee", null, "TEE", null, null, null, 800m, 2, 100, true, null, 1000m, 100m,
            [new NewItemAdjustment("Promotion", promotion, "Eid", "ঈদ", 400m, "Line"), new NewItemAdjustment("BuyXGetY", promotion, "Eid", "ঈদ", 100m, "Order")]));
        var item = Assert.Single(order.Items);
        Assert.Equal(1000m, item.ListPrice);
        Assert.Equal(100m, item.PromotionDiscount);
        Assert.Equal(2, item.Adjustments.Count);
        Assert.Contains(item.Adjustments, a => a.SourceId == promotion && a.SourceType == "Promotion" && a.Amount == 400m && a.Level == "Line");
        Assert.False(item.IsPreorder);
        Assert.Equal(0, item.BackorderedQuantity);
        Assert.Equal(PreorderState.None, order.PreorderState);
    }

    [Fact]
    public void A_line_without_a_list_price_uses_its_unit_price()
    {
        var order = Place(0m, new NewOrderItem(Guid.NewGuid(), Guid.NewGuid(), "Cap", null, "CAP", null, null, null, 300m, 1, 100, true));
        Assert.Equal(300m, order.Items[0].ListPrice);
    }

    [Fact]
    public void A_pre_order_line_waits_for_stock_then_the_order_becomes_ready_once()
    {
        var ship = new DateOnly(2026, 12, 20);
        var order = Place(0m,
            new NewOrderItem(Guid.NewGuid(), Guid.NewGuid(), "Coat", null, "COAT", null, null, null, 2000m, 2, 800, true, null, null, 0m, null, true, ship),
            new NewOrderItem(Guid.NewGuid(), Guid.NewGuid(), "Cap", null, "CAP", null, null, null, 300m, 1, 100, true));
        var coat = order.Items.Single(i => i.Sku == "COAT");
        var cap = order.Items.Single(i => i.Sku == "CAP");

        Assert.Equal(PreorderState.Waiting, order.PreorderState);
        Assert.True(coat.IsPreorder);
        Assert.Equal(2, coat.BackorderedQuantity);
        Assert.Equal(ship, coat.PreorderExpectedShipDate);
        Assert.False(cap.IsPreorder);

        order.AllocatePreorderLine(coat, DateTimeOffset.UtcNow);
        Assert.Equal(0, coat.BackorderedQuantity);
        Assert.NotNull(coat.PreorderAllocatedAt);
        Assert.True(coat.IsPreorder); // the snapshot stays: it was ordered as a pre-order
        Assert.True(order.MarkPreorderReady());
        Assert.Equal(PreorderState.Ready, order.PreorderState);
        Assert.False(order.MarkPreorderReady()); // told once
    }

    [Fact]
    public void A_line_of_another_order_cannot_be_allocated()
    {
        var a = Place(0m, new NewOrderItem(Guid.NewGuid(), Guid.NewGuid(), "Coat", null, "COAT", null, null, null, 2000m, 1, 800, true, null, null, 0m, null, true, null));
        var b = Place(0m, new NewOrderItem(Guid.NewGuid(), Guid.NewGuid(), "Coat", null, "COAT", null, null, null, 2000m, 1, 800, true, null, null, 0m, null, true, null));
        Assert.Throws<InvalidOperationException>(() => a.AllocatePreorderLine(b.Items[0], DateTimeOffset.UtcNow));
    }
}
