using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Juvo.Application.Abstractions;
using Juvo.Domain.Common;
using Juvo.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Juvo.Infrastructure.Persistence.Interceptors;

/// <summary>
/// On every save: stamps created/updated time and user, turns deletes of soft-deletable entities into
/// soft deletes, rejects updates and deletes of append-only tables, and writes an audit log row
/// (who, entity, action, old/new JSON, branch) in the same transaction.
/// </summary>
internal sealed class AuditSaveChangesInterceptor(TimeProvider timeProvider, ICurrentUser currentUser) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly HashSet<string> StampProperties =
    [
        nameof(Entity.RowVersion), nameof(Entity.CreatedAt), nameof(Entity.CreatedBy),
        nameof(Entity.UpdatedAt), nameof(Entity.UpdatedBy),

        // Identity user fields that are secrets or pure bookkeeping.
        "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "NormalizedEmail", "NormalizedUserName", "AccessFailedCount",
    ];

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var now = timeProvider.GetUtcNow();
        var userId = currentUser.UserId;
        var audits = new List<AuditLog>();

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.State is EntityState.Detached or EntityState.Unchanged || entry.Entity is not IAuditable entity)
            {
                continue;
            }

            if (entity is IAppendOnly && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"{entry.Metadata.ClrType.Name} is append-only; it cannot be updated or deleted.");
            }

            var softDeleted = false;
            if (entry.State == EntityState.Deleted && entity is ISoftDeletable softDeletable)
            {
                entry.State = EntityState.Unchanged;
                softDeletable.MarkDeleted(now, userId);
                entry.Property(nameof(ISoftDeletable.IsDeleted)).IsModified = true;
                entry.Property(nameof(ISoftDeletable.DeletedAt)).IsModified = true;
                entry.Property(nameof(ISoftDeletable.DeletedBy)).IsModified = true;
                softDeleted = true;
            }

            if (entry.State == EntityState.Added)
            {
                entity.StampCreated(now, userId);
            }
            else
            {
                entity.StampUpdated(now, userId);
                entry.Property(nameof(Entity.UpdatedAt)).IsModified = true;
                entry.Property(nameof(Entity.UpdatedBy)).IsModified = true;
            }

            if (entity is INotAudited)
            {
                continue;
            }

            var audit = BuildAudit(entry, entity, softDeleted, now, userId);
            if (audit is not null)
            {
                audits.Add(audit);
            }
        }

        if (audits.Count > 0)
        {
            foreach (var audit in audits)
            {
                audit.StampCreated(now, userId);
            }

            context.Set<AuditLog>().AddRange(audits);
        }
    }

    private AuditLog? BuildAudit(EntityEntry entry, IAuditable entity, bool softDeleted, DateTimeOffset now, Guid? userId)
    {
        var entityName = entry.Metadata.ClrType.Name;
        var entityId = entity.Id.ToString();

        switch (entry.State)
        {
            case EntityState.Added:
                return Create(entityName, entityId, AuditAction.Created, null, Snapshot(entry, current: true), now, userId);

            case EntityState.Deleted:
                return Create(entityName, entityId, AuditAction.Deleted, Snapshot(entry, current: false), null, now, userId);

            case EntityState.Modified:
                var oldValues = new Dictionary<string, object?>();
                var newValues = new Dictionary<string, object?>();
                foreach (var property in entry.Properties.Where(p => p.IsModified && !IsIgnored(p)))
                {
                    if (Equals(property.OriginalValue, property.CurrentValue) && !softDeleted)
                    {
                        continue;
                    }

                    oldValues[property.Metadata.Name] = softDeleted && property.Metadata.Name == nameof(ISoftDeletable.IsDeleted)
                        ? false
                        : property.OriginalValue;
                    newValues[property.Metadata.Name] = property.CurrentValue;
                }

                if (newValues.Count == 0)
                {
                    return null;
                }

                var action = softDeleted || (newValues.TryGetValue(nameof(ISoftDeletable.IsDeleted), out var flag) && flag is true)
                    ? AuditAction.Deleted
                    : AuditAction.Updated;
                return Create(entityName, entityId, action, Serialize(oldValues), Serialize(newValues), now, userId);

            default:
                return null;
        }
    }

    private AuditLog Create(string entity, string entityId, AuditAction action, string? oldValue, string? newValue,
        DateTimeOffset now, Guid? userId) =>
        new(userId, currentUser.BranchId, entity, entityId, action, oldValue, newValue,
            currentUser.CorrelationId, currentUser.IpAddress, now);

    private static string Snapshot(EntityEntry entry, bool current)
    {
        var values = new Dictionary<string, object?>();
        foreach (var property in entry.Properties.Where(p => !IsIgnored(p)))
        {
            values[property.Metadata.Name] = current ? property.CurrentValue : property.OriginalValue;
        }

        return Serialize(values);
    }

    private static bool IsIgnored(PropertyEntry property) =>
        StampProperties.Contains(property.Metadata.Name)
        || property.Metadata.PropertyInfo?.GetCustomAttribute<AuditIgnoreAttribute>() is not null;

    private static string Serialize(Dictionary<string, object?> values) => JsonSerializer.Serialize(values, Json);
}
