namespace Juvo.Domain.Common;

/// <summary>
/// An entity that raises domain events. Events are written to the transactional outbox in the same
/// database transaction as the state change, then handled by the background worker.
/// </summary>
public abstract class AggregateRoot : SoftDeletableEntity
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
}
