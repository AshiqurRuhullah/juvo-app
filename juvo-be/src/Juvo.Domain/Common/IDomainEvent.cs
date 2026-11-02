namespace Juvo.Domain.Common;

/// <summary>Something that happened in the domain (status changed, stock moved, payment confirmed).</summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }
}

/// <summary>Convenience base so events get an id and a timestamp.</summary>
public abstract record DomainEvent(DateTimeOffset OccurredAt) : IDomainEvent
{
    public Guid EventId { get; } = Guid.CreateVersion7();
}
