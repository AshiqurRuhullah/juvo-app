namespace Juvo.Domain.Common;

/// <summary>
/// Base for every persisted entity: time-ordered GUID id, created/updated stamps (filled by the
/// audit interceptor) and a SQL Server <c>rowversion</c> concurrency token.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    /// <summary>Optimistic concurrency token (SQL Server rowversion). Never set manually.</summary>
    public byte[] RowVersion { get; private set; } = [];

    /// <summary>Called by the persistence layer when the entity is first saved.</summary>
    public void StampCreated(DateTimeOffset now, Guid? userId)
    {
        CreatedAt = now;
        CreatedBy = userId;
    }

    /// <summary>Called by the persistence layer whenever the entity is modified.</summary>
    public void StampUpdated(DateTimeOffset now, Guid? userId)
    {
        UpdatedAt = now;
        UpdatedBy = userId;
    }
}
