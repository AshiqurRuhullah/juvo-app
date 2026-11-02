using Juvo.Application.Abstractions;
using Juvo.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Juvo.Infrastructure.Persistence.Interceptors;

/// <summary>
/// The write half of branch isolation. For branch-scoped staff, inserting, changing or deleting a branch-scoped row in a
/// branch outside their scope throws <see cref="BranchAccessDeniedException"/> (mapped to 403 and audit-logged), so a
/// developer cannot forget the check in a command. Global-scope staff, customers, anonymous requests and background jobs
/// are not scoped (<see cref="IBranchContext.EnforceScope"/> is false).
/// </summary>
internal sealed class BranchWriteGuardInterceptor(IBranchContext branchContext) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Guard(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Guard(DbContext? context)
    {
        if (context is null || !branchContext.EnforceScope)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var allowed = branchContext.AllowedBranchIds.ToHashSet();

        foreach (var entry in context.ChangeTracker.Entries<IBranchScoped>())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
            {
                continue;
            }

            var name = entry.Metadata.ClrType.Name;
            if (entry.State is EntityState.Added or EntityState.Modified && !allowed.Contains(entry.Entity.BranchId))
            {
                throw new BranchAccessDeniedException(name, entry.Entity.BranchId);
            }

            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                // Moving a row out of, or deleting a row in, a branch the user does not hold is also refused.
                var original = entry.Property(nameof(IBranchScoped.BranchId)).OriginalValue is Guid g ? g : entry.Entity.BranchId;
                if (!allowed.Contains(original))
                {
                    throw new BranchAccessDeniedException(name, original);
                }
            }
        }
    }
}
