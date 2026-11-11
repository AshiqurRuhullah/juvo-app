using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Application.Notifications;
using Juvo.Domain.Returns;

namespace Juvo.Application.Returns;

/// <summary>
/// Reacts to return events from the outbox (at-least-once, so the handler is idempotent). The customer is told through the notification templates (SMS, email) at the steps
/// they care about, in their language; the log row is deduplicated by the outbox message id, so a redelivered event sends nothing twice.
/// </summary>
public sealed class ReturnStatusChangedHandler(NotificationService notifications) : IOutboxMessageHandler
{
    public string MessageType => "domain.ReturnStatusChanged";

    private static readonly Dictionary<string, string> Codes = new(StringComparer.Ordinal)
    {
        [nameof(ReturnStatus.Requested)] = "return.requested",
        [nameof(ReturnStatus.Approved)] = "return.approved",
        [nameof(ReturnStatus.Rejected)] = "return.rejected",
        [nameof(ReturnStatus.PickupScheduled)] = "return.pickup-scheduled",
        [nameof(ReturnStatus.Received)] = "return.received",
        [nameof(ReturnStatus.Refunded)] = "return.refunded",
        [nameof(ReturnStatus.Exchanged)] = "return.exchanged",
        [nameof(ReturnStatus.RefundRejected)] = "return.refund-rejected",
    };

    public Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = JsonSerializer.Deserialize<ReturnStatusChanged>(context.Payload, JsonSerializerOptions.Web) ?? throw new InvalidOperationException("The ReturnStatusChanged message has no content.");
        if (!Codes.TryGetValue(e.To, out var code))
        {
            return Task.CompletedTask;
        }

        var exchange = e.Kind == ReturnKind.Exchange;
        return notifications.NotifyAsync(
            new NotifyRequest(code, e.CustomerMobile, null, e.CustomerId,
                NotifyVariables.Of(("ReturnNumber", e.Number), ("OrderNumber", e.OrderNumber), ("Kind", exchange ? "exchange" : "return"), ("KindBn", exchange ? "এক্সচেঞ্জ" : "রিটার্ন")),
                $"{context.MessageId:N}:{code}"),
            cancellationToken);
    }
}
