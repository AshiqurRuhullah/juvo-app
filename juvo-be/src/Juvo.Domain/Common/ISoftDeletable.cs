namespace Juvo.Domain.Common;

/// <summary>
/// Entities that are archived or deactivated, never erased (products, customers, staff, branches).
/// A global query filter hides deleted rows; deleting through the context sets these fields instead.
/// </summary>
public interface ISoftDeletable
{
    bool IsDeleted { get; }

    DateTimeOffset? DeletedAt { get; }

    Guid? DeletedBy { get; }

    void MarkDeleted(DateTimeOffset now, Guid? userId);

    void Restore();
}
