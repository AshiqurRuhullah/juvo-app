using System.Net;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Accounting;
using Juvo.Domain.Couriers;
using Juvo.Domain.Customers;
using Juvo.Domain.Inventory;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Juvo.Domain.Returns;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Operations;

/// <summary>Everything the report tests need to know about the seeded data set.</summary>
public sealed record ReportSeed(
    string Admin, Guid A, Guid B, DateOnly Today, SeededProduct P1, SeededProduct P2, Guid CourierX, Guid CourierY, Guid Customer1, Guid Customer2, Guid Customer3)
{
    public DateOnly Day(int daysAgo) => Today.AddDays(-daysAgo);

    public string Iso(int daysAgo) => Day(daysAgo).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public string From => Iso(9);

    public string To => Iso(0);

    public string Range => $"from={From}&to={To}";

    public string BranchQuery(Guid? branch) => branch is null ? Range : $"{Range}&branchId={branch}";
}

/// <summary>
/// Builds a known data set through the domain classes (the same ones the application uses), so every report total can be checked against a calculation written out by hand in
/// the tests. See the comment above each order for its numbers. All times are noon in Bangladesh, so no order sits near a day boundary.
/// </summary>
public static class ReportSeeder
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Noon(DateOnly day, int addHours = 0) => new DateTimeOffset(day.Year, day.Month, day.Day, 12, 0, 0, TimeSpan.FromHours(6)).AddHours(addHours);

    private sealed record Item(SeededVariant Variant, SeededProduct Product, int Quantity, decimal Price, decimal? Cost);

    private static readonly ShippingSnapshot Ship = new("Rina", "01711111111", "BD", null, "Dhaka", null, "Dhaka", null, null, null, "House 1", null, null, null, null, null, null);

    private static int _n;

    private static Order Place(Guid branch, Customer c, DateTimeOffset at, OrderSource source, string zone, string method, decimal coupon, decimal manual, decimal delivery, params Item[] items)
    {
        var n = Interlocked.Increment(ref _n);
        var subtotal = items.Sum(i => i.Price * i.Quantity);
        var data = new NewOrder($"JV-RPT-{n:D6}", branch, source, c.Id, c.Mobile, c.FullName, null, method, "DefaultBranch", zone, "BDT", subtotal, coupon, coupon > 0 ? "SAVE" : null, null, manual, delivery, delivery, 0m, 0, Ship,
            null, null, "fp-" + n, "hash-" + n, null, null, false, null, null, null);
        return Order.Place(data, [.. items.Select(i => new NewOrderItem(i.Variant.Id, i.Product.Id, i.Product.Name, null, i.Variant.Sku, i.Variant.Colour, i.Variant.Size, null, i.Price, i.Quantity, 0, true, i.Cost))], at);
    }

    private static void Deliver(Order o, DateTimeOffset placed)
    {
        o.TransitionTo(OrderStatus.Confirmed, placed.AddHours(1), null, "seed", null);
        o.TransitionTo(OrderStatus.Packed, placed.AddHours(2), null, "seed", null);
        o.TransitionTo(OrderStatus.HandedToCourier, placed.AddHours(3), null, "seed", null);
        o.TransitionTo(OrderStatus.InTransit, placed.AddHours(4), null, "seed", null);
        o.TransitionTo(OrderStatus.OutForDelivery, placed.AddHours(5), null, "seed", null);
        o.TransitionTo(OrderStatus.Delivered, placed.AddHours(6), null, "seed", null);
    }

    private static Shipment Parcel(Order o, Courier courier, decimal cost, decimal cod, DateTimeOffset booked, params (ShipmentStatus Status, int Hours)[] path)
    {
        var s = Shipment.Book(new NewShipment(o.Id, o.Number, o.BranchId, courier.Id, courier.Code, courier.Name, ShipmentKind.Forward, null, "CN-" + o.Number, null, null, 500, null, null, null, cod, cost, "Pickup", null, "Pickup address",
            "Rina", "01711111111", "Dhaka", "Dhaka", null), booked, null, "seed");
        foreach (var (status, hours) in path)
        {
            s.MoveTo(status, status.ToString().ToLowerInvariant(), null, status == ShipmentStatus.DeliveryFailed ? "Not at home" : null, null, booked.AddHours(hours), ShipmentEventSource.Staff, "seed");
        }

        return s;
    }

    private static readonly (ShipmentStatus, int)[] DeliveredPath = [(ShipmentStatus.PickedUp, 1), (ShipmentStatus.InTransit, 2), (ShipmentStatus.OutForDelivery, 3)];

    /// <summary>
    /// Today is T (Bangladesh). Range used by the tests: T-9 to T.
    ///   O0 (T-30, outside range) branch A, customer 2, Delivered, P2V1 x1 at 500: only makes customer 2 a returning customer.
    ///   O1 (T-5)  A, Web,      DHAKA,   COD,   customer 1: P1V1 x2 at 1000 (cost 600); coupon 100; delivery 60; Delivered. Parcel courier X cost 50, delivered after 48h, COD 1960 collected and settled (T-3).
    ///   O2 (T-4)  A, Facebook, OUTSIDE, BKASH, customer 2: P2V1 x3 at 500 (cost 300); manual discount 50; delivery 100; Delivered; paid 1550; refund 200 completed T-2. Parcel Y cost 70, delivered after 24h.
    ///   O3 (T-4)  B, Web,      DHAKA,   COD,   customer 1: P1V2 x1 at 1000 + P2V1 x2 at 500; Cancelled ("Customer changed mind"): not a sale. A cancelled parcel (cost 40) is not a charge.
    ///   O4 (T-1)  B, WalkIn,   DHAKA,   COD,   customer 3: P1V1 x1 at 1000 (NO cost recorded); Placed. Parcel Y cost 60, delivery failed.
    ///   O5 (T)    A, Web,      DHAKA,   COD,   customer 3: P2V2 x4 at 500 (cost 300); delivery 60; Confirmed. Parcel X cost 55 in transit, COD 2060 not collected.
    ///   O6 (T-2)  B, Web,      DHAKA,   COD,   customer 1: P2V1 x1 at 500 (cost 300); delivery 60; Delivered. Parcel Y cost 45, delivered after 36h, COD 560 collected, NOT settled.
    /// Returns: R1 SIZE_FIT customer fault (A), R2 and R3 DAMAGED_ITEM seller fault (A and B), all Requested.
    /// Expenses (approved): A 400 Rent (T-3), B 150 Packaging (T-1); a draft of 999 and a submitted one of 777 do not count.
    /// Purchase (through the API): A orders and receives 10 of P1V2 at 550 today.
    /// </summary>
    public static async Task<ReportSeed> BuildAsync(OpsHelper o)
    {
        var admin = await o.H.AdminAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6));
        var s0 = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var a = await o.Api.CreateBranchAsync(admin, "RPA", "Report A");
        var b = await o.Api.CreateBranchAsync(admin, "RPB", "Report B");

        var cat1 = await o.H.CategoryAsync(admin, "Report Cat One");
        var cat2 = await o.H.CategoryAsync(admin, "Report Cat Two");
        var p1 = await o.H.ProductAsync(admin, cat1.Id, "Report Tee One", 1000m, [("Black", "M", 0), ("Black", "L", 0)]);
        var p2 = await o.H.ProductAsync(admin, cat2.Id, "Report Tee Two", 500m, [("Red", "M", 0), ("Red", "L", 0)]);

        // The purchase goes through the real endpoints (it updates stock and the product cost), then the costs are set to known numbers.
        var supplier = (await o.PostAsync("/api/v1/suppliers", admin, new { name = "Report Supplier", paymentTermsDays = 14 }, HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var po = await o.PostAsync("/api/v1/purchasing/orders", admin,
            new { branchId = a, supplierId = supplier, expectedDate = s0, lines = new[] { new { variantId = p1.Variants[1].Id, quantity = 10, unitCost = 550m } } }, HttpStatusCode.Created);
        var poId = po.GetProperty("id").GetGuid();
        var sent = await o.PostAsync($"/api/v1/purchasing/orders/{poId}/send", admin);
        await o.PostAsync($"/api/v1/purchasing/orders/{poId}/receive", admin,
            new { lines = new[] { new { lineId = sent.GetProperty("lines")[0].GetProperty("id").GetGuid(), acceptedQuantity = 10, rejectedQuantity = 0 } } }, HttpStatusCode.Created);

        var customerIds = new List<Guid>();
        var courierX = Guid.Empty;
        var courierY = Guid.Empty;
        await o.WithDbAsync(async db =>
        {
            await db.Products.Where(p => p.Id == p1.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CostPrice, 600m), Ct);
            await db.Products.Where(p => p.Id == p2.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.CostPrice, 300m), Ct);

            var cs = new[] { new Customer("01711000001", ProfileLevel.Full), new Customer("01711000002", ProfileLevel.Quick), new Customer("01711000003", ProfileLevel.Guest) };
            cs[0].UpdateProfile("Customer One", null);
            cs[1].UpdateProfile("Customer Two", null);
            cs[2].UpdateProfile("Customer Three", null);
            db.Customers.AddRange(cs);
            await db.SaveChangesAsync(Ct);
            customerIds.AddRange(cs.Select(c => c.Id));

            CourierDetails Details(string name) => new(name, "SANDBOX", true, 5, true, false, null, null, [], null, 60m, 20m, 1000, null, null, new Dictionary<string, string>());
            var cx = new Courier("RPTX", Details("Courier X"));
            var cy = new Courier("RPTY", Details("Courier Y"));
            db.Couriers.AddRange(cx, cy);
            await db.SaveChangesAsync(Ct);
            courierX = cx.Id;
            courierY = cy.Id;

            Item I(SeededProduct p, int variant, int qty, decimal price, decimal? cost) => new(p.Variants[variant], p, qty, price, cost);

            // O0
            var t0 = Noon(today.AddDays(-30));
            var o0 = Place(a, cs[1], t0, OrderSource.Web, "DHAKA", "COD", 0, 0, 0, I(p2, 0, 1, 500m, 300m));
            Deliver(o0, t0);

            // O1
            var t1 = Noon(today.AddDays(-5));
            var o1 = Place(a, cs[0], t1, OrderSource.Web, "DHAKA", "COD", 100m, 0, 60m, I(p1, 0, 2, 1000m, 600m));
            Deliver(o1, t1);
            var s1 = Parcel(o1, cx, 50m, 1960m, t1.AddHours(2), [.. DeliveredPath, (ShipmentStatus.Delivered, 48)]);
            s1.RecordCodCollected(1960m, t1.AddHours(48));

            // O2
            var t2 = Noon(today.AddDays(-4));
            var o2 = Place(a, cs[1], t2, OrderSource.Facebook, "OUTSIDE", "BKASH", 0, 50m, 100m, I(p2, 0, 3, 500m, 300m));
            Deliver(o2, t2);
            var s2 = Parcel(o2, cy, 70m, 0m, t2.AddHours(2), [.. DeliveredPath, (ShipmentStatus.Delivered, 24)]);
            var pay = Payment.Start(new PaymentInit(o2.Id, o2.Number, a, PaymentKind.Online, "BKASH", "SANDBOX", 1550m, "BDT", null, null, null, null, null, null, null), t2, null, "seed");
            pay.ConfirmPaid("TX1", t2, null, "seed", null, "seed");
            var refund = new Refund(pay, 200m, RefundMode.Manual, "Part refund", null, null, "seed", Noon(today.AddDays(-2)));
            refund.Complete("R-1", Noon(today.AddDays(-2)));

            // O3 (cancelled)
            var t3 = Noon(today.AddDays(-4), 1);
            var o3 = Place(b, cs[0], t3, OrderSource.Web, "DHAKA", "COD", 0, 0, 60m, I(p1, 1, 1, 1000m, 600m), I(p2, 0, 2, 500m, 300m));
            o3.TransitionTo(OrderStatus.Cancelled, t3.AddHours(1), null, "seed", "Customer changed mind");
            var s3 = Parcel(o3, cx, 40m, 0m, t3.AddHours(1));
            s3.MoveTo(ShipmentStatus.Cancelled, "cancelled", null, "order cancelled", null, t3.AddHours(2), ShipmentEventSource.Staff, "seed");

            // O4
            var t4 = Noon(today.AddDays(-1));
            var o4 = Place(b, cs[2], t4, OrderSource.WalkIn, "DHAKA", "COD", 0, 0, 0, I(p1, 0, 1, 1000m, null));
            var s4 = Parcel(o4, cy, 60m, 1000m, t4.AddHours(1), (ShipmentStatus.PickedUp, 1), (ShipmentStatus.InTransit, 2), (ShipmentStatus.OutForDelivery, 3), (ShipmentStatus.DeliveryFailed, 4));

            // O5
            var t5 = Noon(today);
            var o5 = Place(a, cs[2], t5, OrderSource.Web, "DHAKA", "COD", 0, 0, 60m, I(p2, 1, 4, 500m, 300m));
            o5.TransitionTo(OrderStatus.Confirmed, t5.AddMinutes(30), null, "seed", null);
            var s5 = Parcel(o5, cx, 55m, 2060m, t5.AddHours(1), (ShipmentStatus.PickedUp, 1), (ShipmentStatus.InTransit, 2));

            // O6
            var t6 = Noon(today.AddDays(-2));
            var o6 = Place(b, cs[0], t6, OrderSource.Web, "DHAKA", "COD", 0, 0, 60m, I(p2, 0, 1, 500m, 300m));
            Deliver(o6, t6);
            var s6 = Parcel(o6, cy, 45m, 560m, t6.AddHours(2), [.. DeliveredPath, (ShipmentStatus.Delivered, 36)]);
            s6.RecordCodCollected(560m, t6.AddHours(38));

            db.Orders.AddRange(o0, o1, o2, o3, o4, o5, o6);
            db.Shipments.AddRange(s1, s2, s3, s4, s5, s6);
            db.Payments.Add(pay);
            await db.SaveChangesAsync(Ct);
            db.Refunds.Add(refund);

            // The COD cash of O1 was settled on T-3.
            var settleAt = Noon(today.AddDays(-3));
            var settlement = new CodSettlement("CS-RPT-1", a, cx.Id, cx.Name, [new CodSettlementItem(s1.Id, o1.Id, o1.Number, s1.ConsignmentId, 1960m, 50m)], null, "seed");
            settlement.Reconcile([new Juvo.Domain.Couriers.ReceivedLine(s1.Id, 1960m, null)], settleAt, "seed");
            settlement.Settle("BANK-1", settleAt, "seed");
            db.CodSettlements.Add(settlement);
            s1.AttachToSettlement(settlement.Id);

            // Returns.
            ReturnRequest Return(Order order, string reason, ReturnFault fault, int number, int daysAgo)
            {
                var item = order.Items[0];
                return ReturnRequest.Request(new NewReturn($"RET-RPT-{number}", order.BranchId, order.Id, order.Number, order.CustomerId, order.CustomerMobile, order.CustomerName, ReturnKind.Return, reason, null, fault, null, []),
                    [new NewReturnItem(item.Id, item.VariantId, item.ProductId, item.Name, item.Sku, item.ColorName, item.Size, null, 1, item.UnitPrice, item.UnitPrice, null, null)], null, "seed", Noon(today.AddDays(-daysAgo)));
            }

            db.ReturnRequests.AddRange(Return(o2, "SIZE_FIT", ReturnFault.Customer, 1, 3), Return(o1, "DAMAGED_ITEM", ReturnFault.Seller, 2, 2), Return(o6, "DAMAGED_ITEM", ReturnFault.Seller, 3, 1));

            // Expenses.
            var account = new FinanceAccount(a, "Report cash", AccountType.Cash, 0m, today.AddDays(-30), null, null);
            var rent = new ExpenseCategory("Rent", null, null);
            var packaging = new ExpenseCategory("Packaging", null, null);
            db.FinanceAccounts.Add(account);
            db.ExpenseCategories.AddRange(rent, packaging);
            await db.SaveChangesAsync(Ct);
            Expense Exp(string number, Guid branch, Guid category, int daysAgo, decimal amount) =>
                new(number, branch, new ExpenseDetails(category, today.AddDays(-daysAgo), amount, MoneyMethod.Cash, account.Id, null, null, null), null, null);
            var e1 = Exp("EXP-RPT-1", a, rent.Id, 3, 400m);
            var e2 = Exp("EXP-RPT-2", b, packaging.Id, 1, 150m);
            var e3 = Exp("EXP-RPT-3", a, rent.Id, 2, 999m);
            var e4 = Exp("EXP-RPT-4", a, rent.Id, 2, 777m);
            foreach (var e in new[] { e1, e2, e4 })
            {
                e.Submit(Noon(today.AddDays(-4)), null);
            }

            e1.Approve(Noon(today.AddDays(-3)), null, "seed", null, false);
            e2.Approve(Noon(today.AddDays(-1)), null, "seed", null, false);
            db.Expenses.AddRange(e1, e2, e3, e4);

            // Stock (see ReportTests for the arithmetic).
            StockLevel Level(Guid branch, SeededVariant variant, int onHand, int reserved, int reorder)
            {
                var level = new StockLevel(branch, variant.Id);
                level.TryChangeOnHand(onHand);
                if (reserved > 0)
                {
                    level.TryReserve(reserved);
                }

                level.SetReorderLevel(reorder);
                return level;
            }

            db.StockLevels.AddRange(Level(a, p1.Variants[0], 20, 2, 5), Level(a, p2.Variants[0], 3, 1, 5), Level(b, p1.Variants[0], 7, 0, 0), Level(b, p2.Variants[0], 4, 0, 4));
            db.StockMovements.AddRange(
                new StockMovement(a, p1.Variants[0].Id, 20, 20, StockReason.OpeningStock, null, null, null),
                new StockMovement(b, p1.Variants[0].Id, 7, 7, StockReason.OpeningStock, null, null, null),
                new StockMovement(a, p2.Variants[0].Id, -2, 3, StockReason.Damage, null, null, null));
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        return new ReportSeed(admin, a, b, today, p1, p2, courierX, courierY, customerIds[0], customerIds[1], customerIds[2]);
    }
}
