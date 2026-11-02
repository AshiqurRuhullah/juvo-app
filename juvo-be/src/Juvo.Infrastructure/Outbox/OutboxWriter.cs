using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;

namespace Juvo.Infrastructure.Outbox;

internal sealed class OutboxWriter(JuvoDbContext db, TimeProvider timeProvider, ICurrentUser currentUser) : IOutboxWriter
{
    public void Enqueue<TPayload>(string messageType, TPayload payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        db.OutboxMessages.Add(new OutboxMessage(messageType, json, timeProvider.GetUtcNow(), currentUser.CorrelationId));
    }
}
