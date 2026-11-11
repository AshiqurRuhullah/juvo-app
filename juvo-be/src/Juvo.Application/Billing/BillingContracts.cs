using Juvo.Domain.Orders;

namespace Juvo.Application.Billing;

public sealed record TaxRuleDto(Guid Id, string Code, string Name, decimal RatePercent, bool IsInclusive, bool AppliesToDelivery, bool IsActive, bool IsVerified, string? Note);

public sealed record SaveTaxRuleRequest(string? Code, string? Name, decimal RatePercent, bool IsInclusive, bool AppliesToDelivery, bool IsVerified, string? Note);

public sealed record ActivateTaxRuleRequest(bool AcknowledgeUnverified);

public sealed record InvoiceLineDto(string Name, string? NameBn, string Sku, string? Variant, int Quantity, decimal UnitPrice, decimal LineTotal, decimal TaxAmount);

public sealed record CreditNoteSummaryDto(Guid Id, string Number, decimal Amount, decimal TaxAmount, DateTimeOffset IssuedAt);

public sealed record InvoiceSellerDto(string Name, string? Address, string? Phone, string? Email, string? TaxId);

public sealed record InvoiceDto(
    Guid Id, string Number, Guid OrderId, string OrderNumber, Guid BranchId, string BranchCode, DateTimeOffset IssuedAt, string Currency, InvoiceSellerDto Seller, string CustomerName, string CustomerMobile,
    string? CustomerEmail, string BillingAddress, IReadOnlyList<InvoiceLineDto> Lines, decimal Subtotal, decimal Discount, decimal DeliveryCharge, decimal GrandTotal, string? TaxRuleCode, string? TaxRuleName,
    decimal? TaxRatePercent, decimal DeliveryTax, decimal TaxTotal, decimal NetTotal, string PaymentMethod, PaymentStatus PaymentStatus, decimal CreditedTotal, IReadOnlyList<CreditNoteSummaryDto> CreditNotes);

public sealed record InvoiceListQuery(Guid? BranchId, DateTimeOffset? From, DateTimeOffset? To);

public sealed record InvoiceListItemDto(Guid Id, string Number, Guid OrderId, string OrderNumber, Guid BranchId, string BranchCode, string CustomerName, decimal GrandTotal, decimal TaxTotal, DateTimeOffset IssuedAt);

public sealed record CreditNoteDto(
    Guid Id, string Number, Guid InvoiceId, string InvoiceNumber, Guid OrderId, string OrderNumber, Guid BranchId, string BranchCode, decimal Amount, decimal TaxAmount, string Currency, string? Reason,
    DateTimeOffset IssuedAt);

public sealed record CreditNoteListQuery(Guid? BranchId, DateTimeOffset? From, DateTimeOffset? To);

/// <summary>Everything the PDF renderer needs. Built from an invoice or credit note; the renderer never reads the database.</summary>
public sealed record PdfDocumentModel(
    bool IsCreditNote, string Title, string Number, string? ReferenceNumber, DateTimeOffset IssuedAt, string Language, InvoiceSellerDto Seller, string CustomerName, string CustomerMobile, string? CustomerEmail,
    string BillingAddress, string OrderNumber, string Currency, IReadOnlyList<PdfLine> Lines, decimal Subtotal, decimal Discount, decimal DeliveryCharge, decimal GrandTotal, string? TaxLabel, decimal TaxTotal,
    decimal NetTotal, string PaymentMethod, string PaymentStatus, string? Note);

public sealed record PdfLine(string Name, string? Variant, string Sku, int Quantity, decimal UnitPrice, decimal LineTotal);

/// <summary>Draws a branded A4 document. Implemented in Infrastructure (the library choice and fonts are recorded in docs/assumptions.md section Q).</summary>
public interface IDocumentPdfRenderer
{
    byte[] Render(PdfDocumentModel model);
}
