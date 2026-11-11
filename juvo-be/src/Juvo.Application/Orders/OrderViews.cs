using Juvo.Application.Abstractions;
using Juvo.Domain.Orders;

namespace Juvo.Application.Orders;

/// <summary>Turns orders into the shapes the API returns. Customers and the public never get actor names or internal reasons.</summary>
internal static class OrderViews
{
    public static string? ImageUrl(string? path, IFileStorage storage) => path is null ? null : storage.GetPublicUrl(ImageRenditions.Card(path));

    public static ShippingDto Shipping(Order o) =>
        new(o.ShipName, o.ShipPhone, o.ShipCountryCode, o.ShipDivision, o.ShipDistrict, o.ShipUpazila, o.ShipArea, o.ShipAddressLine, o.ShipLandmark, o.ShipStateProvince, o.ShipCity,
            o.ShipPostalCode, o.ShipLatitude, o.ShipLongitude);

    public static MoneyDto Money(Order o) =>
        new(o.Currency, o.Subtotal, o.CouponDiscount, o.CouponCode, o.ManualDiscount, o.DeliveryCharge, o.GrandTotal, o.AdvanceRequired);

    public static List<OrderLineDto> Lines(Order o, IFileStorage storage) =>
        [.. o.Items.OrderBy(i => i.CreatedAt).ThenBy(i => i.Sku).Select(i =>
            new OrderLineDto(i.VariantId, i.ProductId, i.Name, i.NameBn, i.Sku, i.ColorName, i.Size, ImageUrl(i.ImagePath, storage), i.UnitPrice, i.Quantity, i.LineTotal))];

    /// <summary>Full timeline for staff.</summary>
    public static List<TimelineDto> StaffTimeline(Order o) =>
        [.. o.History.OrderBy(h => h.At).ThenBy(h => h.Track).Select(h => new TimelineDto(h.Track, h.FromStatus, h.ToStatus, h.Reason, h.ActorName, h.At))];

    /// <summary>Timeline for the customer: statuses and times; a reason only when the customer gave it themselves.</summary>
    public static List<TimelineDto> CustomerTimeline(Order o) =>
        [.. o.History.OrderBy(h => h.At).ThenBy(h => h.Track).Select(h =>
            new TimelineDto(h.Track, h.FromStatus, h.ToStatus, h.ActorId == o.CustomerId ? h.Reason : null, null, h.At))];

    public static bool CanCancel(Order o) => OrderStateMachine.CanCancel(o.Status);

    public static List<NoteDto> CustomerNotes(Order o) =>
        [.. o.Notes.Where(n => n.Kind == NoteKind.Customer).OrderBy(n => n.At).Select(n => new NoteDto(n.Id, n.Kind, n.Text, null, n.At))];
}
