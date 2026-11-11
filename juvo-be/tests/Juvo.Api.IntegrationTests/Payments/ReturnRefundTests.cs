using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Returns;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Payments;

/// <summary>Shared helpers: an order paid online and delivered, with its customer, ready for a return.</summary>
internal static class OnlineDelivery
{
    public static async Task<DeliveredOrder> DeliverPaidAsync(PaymentHelper p, ReturnHelper r, string admin, Guid variantId, int quantity)
    {
        var order = await p.PlaceAsync(variantId, quantity);
        await p.PayAsync(order); // paid and auto-confirmed
        await p.O.ActAsync(admin, order.OrderId, "pack");
        foreach (var step in new[] { "HandedToCourier", "InTransit", "OutForDelivery", "Delivered" })
        {
            await p.O.ActAsync(admin, order.OrderId, "transition", new { to = step });
        }

        var detail = await p.O.OrderAsync(admin, order.OrderId);
        return new DeliveredOrder(order.OrderId, order.Number, order.Mobile, await p.H.CustomerTokenAsync(order.Mobile), variantId, detail.GetProperty("money").GetProperty("deliveryCharge").GetDecimal());
    }
}

public class ReturnRefundTests(PaymentFactory factory) : IClassFixture<PaymentFactory>
{
    private readonly PaymentHelper _p = new(new OrderHelper(new StoreHelper(factory)));

    private ReturnHelper R => new(_p.O);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [Fact]
    public async Task The_refund_of_a_return_on_an_online_order_goes_back_through_the_gateway_and_gets_a_credit_note()
    {
        var (admin, product) = await _p.ProductAsync(price: 1000m);
        var d = await OnlineDelivery.DeliverPaidAsync(_p, R, admin, product.Variants[0].Id, 2);
        var returnId = (await R.RequestAsync(d, quantity: 1)).GetProperty("id").GetGuid();
        await R.ToInspectedAsync(admin, returnId);

        var refunded = await R.RefundAsync(admin, returnId, method: "OriginalPayment", reference: "RETURN-REF-1");
        Assert.Equal("Refunded", Str(refunded, "status"));
        var amount = refunded.GetProperty("refundPaid").GetProperty("amount").GetDecimal();

        var payments = await _p.ForOrderAsync(admin, d.OrderId);
        var paymentId = payments.EnumerateArray().Single().GetProperty("id").GetGuid();
        var detail = await _p.PaymentAsync(admin, paymentId);
        Assert.Equal(amount, detail.GetProperty("refundedAmount").GetDecimal());
        Assert.Equal("PartiallyRefunded", Str(detail, "status"));
        var refund = detail.GetProperty("refunds").EnumerateArray().Single();
        Assert.Equal("Gateway", Str(refund, "mode")); // the gateway was asked, not only a note made
        Assert.Equal("Completed", Str(refund, "status"));
        Assert.StartsWith("SBX-RF-", Str(refund, "reference"), StringComparison.Ordinal);
        Assert.StartsWith("CN-HO-", Str(refund, "creditNoteNumber"), StringComparison.Ordinal);
        Assert.Equal(returnId, refund.GetProperty("returnRequestId").GetGuid());
        Assert.Equal("PartiallyRefunded", Str(await _p.OrderAsync(admin, d.OrderId), "paymentStatus"));

        // No double refund: the return is closed, and the payment cannot be refunded past what is left.
        await StoreHelper.ExpectCodeAsync(await R.TryActAsync(admin, returnId, "refund", new { method = "OriginalPayment", reference = "AGAIN" }), "RETURN.ALREADY_REFUNDED");
        Assert.Equal(1, await _p.DbAsync(db => db.Refunds.IgnoreQueryFilters(["BranchScope"]).CountAsync(x => x.OrderId == d.OrderId, Ct)));
        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/{paymentId}/refunds", new { amount = detail.GetProperty("amount").GetDecimal(), mode = "Gateway", reason = "Too much" }), "PAYMENT.REFUND_EXCEEDS_PAID");
    }

    [Fact]
    public async Task A_cash_refund_of_a_return_on_an_online_order_is_a_manual_refund_with_the_staff_reference()
    {
        var (admin, product) = await _p.ProductAsync(price: 800m);
        var d = await OnlineDelivery.DeliverPaidAsync(_p, R, admin, product.Variants[0].Id, 1);
        var returnId = (await R.RequestAsync(d)).GetProperty("id").GetGuid();
        await R.ToInspectedAsync(admin, returnId);

        await R.RefundAsync(admin, returnId, method: "Cash", reference: "CASH-AT-COUNTER-4");

        var paymentId = (await _p.ForOrderAsync(admin, d.OrderId)).EnumerateArray().Single().GetProperty("id").GetGuid();
        var refund = (await _p.PaymentAsync(admin, paymentId)).GetProperty("refunds").EnumerateArray().Single();
        Assert.Equal("Manual", Str(refund, "mode"));
        Assert.Equal("CASH-AT-COUNTER-4", Str(refund, "reference"));
        Assert.Equal("Completed", Str(refund, "status"));
    }

    [Fact]
    public async Task The_refund_of_a_return_on_a_cash_order_creates_the_cash_payment_record_a_manual_refund_and_a_credit_note()
    {
        var (admin, product) = await _p.ProductAsync(price: 900m);
        var d = await R.DeliverAsync(admin, product.Variants[0].Id, 1);
        Assert.Equal(0, (await _p.ForOrderAsync(admin, d.OrderId)).GetArrayLength());
        var returnId = (await R.RequestAsync(d)).GetProperty("id").GetGuid();
        await R.ToInspectedAsync(admin, returnId);

        var refunded = await R.RefundAsync(admin, returnId, method: "Cash", reference: "RCPT-COD-1");

        var payments = await _p.ForOrderAsync(admin, d.OrderId);
        var cash = payments.EnumerateArray().Single();
        Assert.Equal("Cod", Str(cash, "kind"));
        var detail = await _p.PaymentAsync(admin, cash.GetProperty("id").GetGuid());
        Assert.Equal(refunded.GetProperty("refundPaid").GetProperty("amount").GetDecimal(), detail.GetProperty("refundedAmount").GetDecimal());
        var refund = detail.GetProperty("refunds").EnumerateArray().Single();
        Assert.Equal("Manual", Str(refund, "mode"));
        Assert.StartsWith("CN-HO-", Str(refund, "creditNoteNumber"), StringComparison.Ordinal);
        var notes = await _p.H.JsonAsync(HttpMethod.Get, $"/api/v1/billing/credit-notes?search={Str(refund, "creditNoteNumber")}", HttpStatusCode.OK, bearer: admin);
        Assert.Single(notes.GetProperty("items").EnumerateArray());
    }
}

public class GatewayRefundFailureTests(FailingRefundPaymentFactory factory) : IClassFixture<FailingRefundPaymentFactory>
{
    private readonly PaymentHelper _p = new(new OrderHelper(new StoreHelper(factory)));

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [Fact]
    public async Task A_gateway_that_refuses_the_refund_leaves_the_return_unrefunded_and_the_money_refundable()
    {
        var (admin, product) = await _p.ProductAsync(price: 1000m);
        var r = new ReturnHelper(_p.O);
        var d = await OnlineDelivery.DeliverPaidAsync(_p, r, admin, product.Variants[0].Id, 1);
        var returnId = (await r.RequestAsync(d)).GetProperty("id").GetGuid();
        await r.ToInspectedAsync(admin, returnId);

        await StoreHelper.ExpectCodeAsync(await r.TryActAsync(admin, returnId, "refund", new { method = "OriginalPayment", reference = "TRY-1" }), "PAYMENT.REFUND_FAILED");

        Assert.Equal("Inspected", Str(await r.DetailAsync(admin, returnId), "status")); // the return did not move
        var paymentId = (await _p.ForOrderAsync(admin, d.OrderId)).EnumerateArray().Single().GetProperty("id").GetGuid();
        var detail = await _p.PaymentAsync(admin, paymentId);
        Assert.Equal(0m, detail.GetProperty("refundedAmount").GetDecimal());
        Assert.Equal(0m, detail.GetProperty("refundPendingAmount").GetDecimal());
        Assert.Equal("Paid", Str(detail, "status"));
        Assert.Empty(detail.GetProperty("refunds").EnumerateArray()); // the failed attempt was rolled back with the rest

        // The staff can still refund it by hand.
        await r.RefundAsync(admin, returnId, method: "BankTransfer", reference: "BANK-1");
        Assert.Equal("Refunded", Str(await r.DetailAsync(admin, returnId), "status"));
    }

    [Fact]
    public async Task A_standalone_gateway_refund_that_fails_is_recorded_as_failed_and_frees_the_money()
    {
        var (admin, product) = await _p.ProductAsync(price: 1000m);
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var paymentId = (await _p.PayAsync(order)).GetProperty("paymentId").GetGuid();

        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/{paymentId}/refunds", new { amount = 100m, mode = "Gateway", reason = "Try" }), "PAYMENT.REFUND_FAILED");

        var detail = await _p.PaymentAsync(admin, paymentId);
        var refund = detail.GetProperty("refunds").EnumerateArray().Single();
        Assert.Equal("Failed", Str(refund, "status"));
        Assert.Equal(order.Total, detail.GetProperty("refundable").GetDecimal());
        Assert.Equal("Paid", Str(detail, "status"));
    }
}

public class GatewayPendingRefundTests(PendingRefundPaymentFactory factory) : IClassFixture<PendingRefundPaymentFactory>
{
    private readonly PaymentHelper _p = new(new OrderHelper(new StoreHelper(factory)));

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [Fact]
    public async Task A_gateway_refund_that_finishes_later_stays_pending_until_staff_complete_it()
    {
        var (admin, product) = await _p.ProductAsync(price: 1000m);
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var paymentId = (await _p.PayAsync(order)).GetProperty("paymentId").GetGuid();

        var refund = await _p.RefundAsync(admin, paymentId, 300m);
        Assert.Equal("Pending", Str(refund, "status"));
        Assert.StartsWith("SBX-RF-", Str(refund, "reference"), StringComparison.Ordinal); // the gateway's own id is kept
        var during = await _p.PaymentAsync(admin, paymentId);
        Assert.Equal("Paid", Str(during, "status"));
        Assert.Equal(300m, during.GetProperty("refundPendingAmount").GetDecimal());

        var done = await _p.StaffPostAsync(admin, $"/api/v1/payments/refunds/{refund.GetProperty("id").GetGuid()}/complete", new { reference = "GW-CONFIRMED-1" });
        Assert.Equal("Completed", Str(done, "status"));
        Assert.StartsWith("CN-HO-", Str(done, "creditNoteNumber"), StringComparison.Ordinal);
        Assert.Equal("PartiallyRefunded", Str(await _p.PaymentAsync(admin, paymentId), "status"));
    }
}
