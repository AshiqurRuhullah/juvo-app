namespace Juvo.Domain.Common;

/// <summary>An <see cref="Entity"/> that supports soft delete.</summary>
public abstract class SoftDeletableEntity : Entity, ISoftDeletable
{
    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public void MarkDeleted(DateTimeOffset now, Guid? userId)
    {
        IsDeleted = true;
        DeletedAt = now;
        DeletedBy = userId;
    }

    public void Restore()
    {
        IsDeleted = false;
        DeletedAt = null;
        DeletedBy = null;
    }
}
