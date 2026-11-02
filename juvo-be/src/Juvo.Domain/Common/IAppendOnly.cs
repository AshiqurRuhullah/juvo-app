namespace Juvo.Domain.Common;

/// <summary>
/// Marker for tables that are append-only: order status history, stock movements, payments,
/// refunds, shipment events and the audit log. The persistence layer rejects updates and deletes.
/// </summary>
public interface IAppendOnly
{
}
