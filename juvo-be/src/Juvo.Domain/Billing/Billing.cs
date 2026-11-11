using Juvo.Domain.Common;

namespace Juvo.Domain.Billing;

/// <summary>
/// A tax rule (module U). Prices are stored tax-inclusive (BR-005, SRS 3.5), so a rule only reports how much of the price is tax: it never changes a total.
/// At most one rule is active. The seeded 15% VAT sample is inactive and marked unverified until the owner's accountant confirms it.
/// </summary>
public sealed class TaxRule : Entity
{
    private TaxRule()
    {
    }

    public TaxRule(string code, string name, decimal ratePercent, bool appliesToDelivery, bool isVerified, string? note)
    {
        Code = code.Trim().ToUpperInvariant();
        Update(name, ratePercent, appliesToDelivery, isVerified, note);
    }

    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>For example 15 for 15 percent.</summary>
    public decimal RatePercent { get; private set; }

    /// <summary>Always true for now: the stored prices already contain the tax. A rule that adds tax on top would change every total and is not supported yet.</summary>
    public bool IsInclusive { get; private set; } = true;

    public bool AppliesToDelivery { get; private set; } = true;

    public bool IsActive { get; private set; }

    /// <summary>False until someone confirmed the rate and scope with the accountant.</summary>
    public bool IsVerified { get; private set; }

    public string? Note { get; private set; }

    public void Update(string name, decimal ratePercent, bool appliesToDelivery, bool isVerified, string? note)
    {
        Name = name.Trim();
        RatePercent = ratePercent;
        AppliesToDelivery = appliesToDelivery;
        IsVerified = isVerified;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public void SetActive(bool active) => IsActive = active;
}

/// <summary>The tax inside an order, worked out line by line.</summary>
public sealed record TaxBreakdown(IReadOnlyList<decimal> LineTax, decimal DeliveryTax, decimal TaxTotal, decimal NetTotal)
{
    public static TaxBreakdown None(decimal grandTotal) => new([], 0m, 0m, grandTotal);
}

/// <summary>Tax maths for tax-inclusive prices. Rounds each part to 2 decimals (half away from zero) and never changes the grand total.</summary>
public static class TaxMath
{
    /// <summary>The tax contained in a gross (tax-inclusive) amount: <c>gross * rate / (100 + rate)</c>.</summary>
    public static decimal TaxIn(decimal gross, decimal ratePercent) =>
        gross <= 0 || ratePercent <= 0 ? 0m : Math.Round(gross * ratePercent / (100m + ratePercent), 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Splits the discount over the lines in proportion to their value (the last line takes the rounding rest), then takes the tax out of each discounted line
    /// and, when the rule applies to delivery, out of the delivery charge. <paramref name="grandTotal"/> is items minus discount plus delivery.
    /// </summary>
    public static TaxBreakdown ForOrder(IReadOnlyList<decimal> lineTotals, decimal discount, decimal deliveryCharge, decimal ratePercent, bool appliesToDelivery)
    {
        var subtotal = lineTotals.Sum();
        var grand = subtotal - discount + deliveryCharge;
        if (ratePercent <= 0 || lineTotals.Count == 0)
        {
            return new TaxBreakdown([.. lineTotals.Select(_ => 0m)], 0m, 0m, grand);
        }

        var shares = new List<decimal>(lineTotals.Count);
        var given = 0m;
        for (var i = 0; i < lineTotals.Count; i++)
        {
            var share = subtotal == 0 ? 0m : i == lineTotals.Count - 1 ? discount - given : Math.Round(discount * lineTotals[i] / subtotal, 2, MidpointRounding.AwayFromZero);
            shares.Add(share);
            given += share;
        }

        var lineTax = lineTotals.Select((t, i) => TaxIn(Math.Max(0m, t - shares[i]), ratePercent)).ToList();
        var deliveryTax = appliesToDelivery ? TaxIn(deliveryCharge, ratePercent) : 0m;
        var total = lineTax.Sum() + deliveryTax;
        return new TaxBreakdown(lineTax, deliveryTax, total, grand - total);
    }
}

/// <summary>Counter behind the gap-free document numbers per branch, kind and month (<c>INV-DHK01-2611-000001</c>, <c>CN-DHK01-2611-000001</c>).</summary>
public sealed class DocumentSequence : Entity, INotAudited
{
    private DocumentSequence()
    {
    }

    public DocumentSequence(Guid branchId, string kind, string period, int lastNumber)
    {
        BranchId = branchId;
        Kind = kind;
        Period = period;
        LastNumber = lastNumber;
    }

    public Guid BranchId { get; private set; }

    /// <summary><c>INV</c> or <c>CN</c>.</summary>
    public string Kind { get; private set; } = string.Empty;

    /// <summary>Year and month as <c>yyMM</c>.</summary>
    public string Period { get; private set; } = string.Empty;

    public int LastNumber { get; private set; }
}

public sealed record InvoiceSeller(string Name, string? Address, string? Phone, string? Email, string? TaxId);

public sealed record NewInvoiceLine(string Name, string? NameBn, string Sku, string? Variant, int Quantity, decimal UnitPrice, decimal LineTotal, decimal TaxAmount);

public sealed record NewInvoice(
    string Number, Guid BranchId, Guid OrderId, string OrderNumber, Guid CustomerId, string CustomerName, string CustomerMobile, string? CustomerEmail, string BillingAddress, InvoiceSeller Seller,
    string Currency, decimal Subtotal, decimal Discount, decimal DeliveryCharge, decimal GrandTotal, string? TaxRuleCode, string? TaxRuleName, decimal? TaxRatePercent, decimal DeliveryTax, decimal TaxTotal,
    IReadOnlyList<NewInvoiceLine> Lines, Guid? IssuedBy, string? IssuedByName);

/// <summary>
/// A tax invoice (module U). Issued once per order, gap-free per branch, and immutable: it is append-only and the database interceptor refuses any update or delete.
/// Everything on it is a snapshot of the moment it was issued (seller, customer, lines, tax), so later edits to products or settings never change it.
/// </summary>
public sealed class Invoice : Entity, IBranchScoped, IAppendOnly
{
    private readonly List<InvoiceLine> _lines = [];

    private Invoice()
    {
    }

    public string Number { get; private set; } = string.Empty;

    public Guid BranchId { get; private set; }

    public Guid OrderId { get; private set; }

    public string OrderNumber { get; private set; } = string.Empty;

    public Guid CustomerId { get; private set; }

    [AuditIgnore]
    public string CustomerName { get; private set; } = string.Empty;

    [AuditIgnore]
    public string CustomerMobile { get; private set; } = string.Empty;

    [AuditIgnore]
    public string? CustomerEmail { get; private set; }

    [AuditIgnore]
    public string BillingAddress { get; private set; } = string.Empty;

    public string SellerName { get; private set; } = string.Empty;

    public string? SellerAddress { get; private set; }

    public string? SellerPhone { get; private set; }

    public string? SellerEmail { get; private set; }

    /// <summary>BIN or TIN of the seller, printed on the invoice when the owner set it.</summary>
    public string? SellerTaxId { get; private set; }

    public string Currency { get; private set; } = "BDT";

    public decimal Subtotal { get; private set; }

    public decimal Discount { get; private set; }

    public decimal DeliveryCharge { get; private set; }

    public decimal GrandTotal { get; private set; }

    public string? TaxRuleCode { get; private set; }

    public string? TaxRuleName { get; private set; }

    public decimal? TaxRatePercent { get; private set; }

    public decimal DeliveryTax { get; private set; }

    /// <summary>The tax contained in <see cref="GrandTotal"/> (zero when no rule was active).</summary>
    public decimal TaxTotal { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public Guid? IssuedBy { get; private set; }

    public string? IssuedByName { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines;

    public static Invoice Issue(NewInvoice d, DateTimeOffset now)
    {
        var invoice = new Invoice
        {
            Number = d.Number,
            BranchId = d.BranchId,
            OrderId = d.OrderId,
            OrderNumber = d.OrderNumber,
            CustomerId = d.CustomerId,
            CustomerName = d.CustomerName,
            CustomerMobile = d.CustomerMobile,
            CustomerEmail = d.CustomerEmail,
            BillingAddress = d.BillingAddress,
            SellerName = d.Seller.Name,
            SellerAddress = d.Seller.Address,
            SellerPhone = d.Seller.Phone,
            SellerEmail = d.Seller.Email,
            SellerTaxId = d.Seller.TaxId,
            Currency = d.Currency,
            Subtotal = d.Subtotal,
            Discount = d.Discount,
            DeliveryCharge = d.DeliveryCharge,
            GrandTotal = d.GrandTotal,
            TaxRuleCode = d.TaxRuleCode,
            TaxRuleName = d.TaxRuleName,
            TaxRatePercent = d.TaxRatePercent,
            DeliveryTax = d.DeliveryTax,
            TaxTotal = d.TaxTotal,
            IssuedAt = now,
            IssuedBy = d.IssuedBy,
            IssuedByName = d.IssuedByName,
        };
        var order = 0;
        foreach (var line in d.Lines)
        {
            invoice._lines.Add(new InvoiceLine(invoice.Id, order++, line));
        }

        return invoice;
    }
}

public sealed class InvoiceLine : Entity, IAppendOnly, INotAudited
{
    private InvoiceLine()
    {
    }

    internal InvoiceLine(Guid invoiceId, int sortOrder, NewInvoiceLine l)
    {
        InvoiceId = invoiceId;
        SortOrder = sortOrder;
        Name = l.Name;
        NameBn = l.NameBn;
        Sku = l.Sku;
        Variant = l.Variant;
        Quantity = l.Quantity;
        UnitPrice = l.UnitPrice;
        LineTotal = l.LineTotal;
        TaxAmount = l.TaxAmount;
    }

    public Guid InvoiceId { get; private set; }

    public int SortOrder { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string? Variant { get; private set; }

    public int Quantity { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal LineTotal { get; private set; }

    public decimal TaxAmount { get; private set; }
}

/// <summary>
/// A credit note (module U): issued for a completed refund, gap-free per branch, immutable. It carries the share of the invoice's tax that the refund covers.
/// One credit note per refund (unique).
/// </summary>
public sealed class CreditNote : Entity, IBranchScoped, IAppendOnly
{
    private CreditNote()
    {
    }

    public CreditNote(string number, Invoice invoice, Guid refundId, decimal amount, decimal taxAmount, string? reason, Guid? by, string? byName, DateTimeOffset now)
    {
        Number = number;
        BranchId = invoice.BranchId;
        InvoiceId = invoice.Id;
        InvoiceNumber = invoice.Number;
        OrderId = invoice.OrderId;
        OrderNumber = invoice.OrderNumber;
        RefundId = refundId;
        Amount = amount;
        TaxAmount = taxAmount;
        Currency = invoice.Currency;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        IssuedBy = by;
        IssuedByName = byName;
        IssuedAt = now;
    }

    public string Number { get; private set; } = string.Empty;

    public Guid BranchId { get; private set; }

    public Guid InvoiceId { get; private set; }

    public string InvoiceNumber { get; private set; } = string.Empty;

    public Guid OrderId { get; private set; }

    public string OrderNumber { get; private set; } = string.Empty;

    public Guid RefundId { get; private set; }

    public decimal Amount { get; private set; }

    public decimal TaxAmount { get; private set; }

    public string Currency { get; private set; } = "BDT";

    public string? Reason { get; private set; }

    public Guid? IssuedBy { get; private set; }

    public string? IssuedByName { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }
}
