using Juvo.Domain.Common;

namespace Juvo.Domain.Platform;

public enum AuditAction
{
    Created = 1,
    Updated = 2,
    Deleted = 3,

    /// <summary>A request was refused (wrong branch, missing permission). Written so attempts can be reviewed.</summary>
    AccessDenied = 4,

    /// <summary>Personal data (mobile, email, addresses) was shown to staff (NFR-012, FR-E08 access log).</summary>
    Viewed = 5,
}

/// <summary>Who changed what and when. Append-only; staff cannot edit it (NFR-012). Global table; every row records the branch.</summary>
public sealed class AuditLog : Entity, IAppendOnly, INotAudited
{
    private AuditLog()
    {
    }

    public AuditLog(Guid? userId, Guid? branchId, string entity, string entityId, AuditAction action,
        string? oldValue, string? newValue, string? correlationId, string? ipAddress, DateTimeOffset occurredAt)
    {
        UserId = userId;
        BranchId = branchId;
        Entity = entity;
        EntityId = entityId;
        Action = action;
        OldValue = oldValue;
        NewValue = newValue;
        CorrelationId = correlationId;
        IpAddress = ipAddress;
        OccurredAt = occurredAt;
    }

    public Guid? UserId { get; private set; }

    public Guid? BranchId { get; private set; }

    public string Entity { get; private set; } = string.Empty;

    public string EntityId { get; private set; } = string.Empty;

    public AuditAction Action { get; private set; }

    /// <summary>JSON of the previous values (updates and deletes).</summary>
    public string? OldValue { get; private set; }

    /// <summary>JSON of the new values (creates and updates).</summary>
    public string? NewValue { get; private set; }

    public string? CorrelationId { get; private set; }

    public string? IpAddress { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
}
