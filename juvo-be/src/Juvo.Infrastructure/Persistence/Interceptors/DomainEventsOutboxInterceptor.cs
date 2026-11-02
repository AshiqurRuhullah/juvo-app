using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Domain.Common;
using Juvo.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Juvo.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Moves domain events raised by aggregates into the outbox table, inside the same save (same transaction)
/// as the state change. Message type is <c>domain.&lt;EventTypeName&gt;</c>; a handler claims it by that name.
/// </summary>
internal sealed class DomainEventsOutboxInterceptor(TimeProvider timeProvider, ICurrentUser currentUser) : SaveChangesInterceptor
{
    public const string MessageTypePrefix = "domain.";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var now = timeProvider.GetUtcNow();
        var messages = new List<OutboxMessage>();

        foreach (var root in context.ChangeTracker.Entries<AggregateRoot>().Select(e => e.Entity).ToList())
        {
            foreach (var domainEvent in root.DomainEvents)
            {
                var payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), JsonSerializerOptions.Web);
                messages.Add(new OutboxMessage(MessageTypePrefix + domainEvent.GetType().Name, payload, now, currentUser.CorrelationId));
            }

            root.ClearDomainEvents();
        }

        if (messages.Count > 0)
        {
            context.Set<OutboxMessage>().AddRange(messages);
        }
    }
}
