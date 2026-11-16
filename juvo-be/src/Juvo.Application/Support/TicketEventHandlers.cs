using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Application.Notifications;
using Juvo.Domain.Support;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Support;

/// <summary>
/// Turns ticket events from the outbox into customer messages (FR-Y04) through the notification templates, in the customer's language and with the same channel rules as order and
/// return messages. The log row is deduplicated by the outbox message id, so a redelivered event sends nothing twice. Internal notes raise no event and so never reach a customer.
/// </summary>
internal static class TicketMessages
{
    public const int MaxReplyChars = 1500;

    public static Task NotifyAsync(NotificationService notifications, string code, Guid messageId, string number, string subject, string mobile, string? email, Guid? customerId, string? name, string? reply,
        CancellationToken cancellationToken) =>
        notifications.NotifyAsync(
            new NotifyRequest(code, mobile, email, customerId,
                NotifyVariables.Of(("TicketNumber", number), ("Subject", subject), ("Name", name ?? string.Empty), ("Reply", reply ?? string.Empty)), $"{messageId:N}:{code}"),
            cancellationToken);
}

public sealed class TicketCreatedHandler(NotificationService notifications) : IOutboxMessageHandler
{
    public string MessageType => "domain.TicketCreated";

    public Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = JsonSerializer.Deserialize<TicketCreated>(context.Payload, JsonSerializerOptions.Web) ?? throw new InvalidOperationException("The TicketCreated message has no content.");
        return TicketMessages.NotifyAsync(notifications, "ticket.received", context.MessageId, e.Number, e.Subject, e.CustomerMobile, e.CustomerEmail, e.CustomerId, e.CustomerName, null, cancellationToken);
    }
}

public sealed class TicketRepliedHandler(NotificationService notifications, IApplicationDbContext db) : IOutboxMessageHandler
{
    public string MessageType => "domain.TicketReplied";

    public async Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = JsonSerializer.Deserialize<TicketReplied>(context.Payload, JsonSerializerOptions.Web) ?? throw new InvalidOperationException("The TicketReplied message has no content.");
        var message = await db.TicketMessages.AsNoTracking().Where(m => m.Id == e.MessageId && m.Kind == TicketMessageKind.Staff).Select(m => m.Text).FirstOrDefaultAsync(cancellationToken);
        if (message is null)
        {
            return; // not a public reply (or gone): nothing to tell the customer
        }

        var reply = message.Length > TicketMessages.MaxReplyChars ? message[..TicketMessages.MaxReplyChars] + "..." : message;
        await TicketMessages.NotifyAsync(notifications, "ticket.replied", context.MessageId, e.Number, e.Subject, e.CustomerMobile, e.CustomerEmail, e.CustomerId, e.CustomerName, reply, cancellationToken);
    }
}

public sealed class TicketResolvedHandler(NotificationService notifications) : IOutboxMessageHandler
{
    public string MessageType => "domain.TicketResolved";

    public Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = JsonSerializer.Deserialize<TicketResolved>(context.Payload, JsonSerializerOptions.Web) ?? throw new InvalidOperationException("The TicketResolved message has no content.");
        return TicketMessages.NotifyAsync(notifications, "ticket.resolved", context.MessageId, e.Number, e.Subject, e.CustomerMobile, e.CustomerEmail, e.CustomerId, e.CustomerName, null, cancellationToken);
    }
}
