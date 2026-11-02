using System.Linq.Expressions;
using Juvo.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Infrastructure.Persistence;

/// <summary>Model rules shared by every DbContext of the platform (the real one and the ones used in tests).</summary>
internal static class ModelDefaults
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clr = entityType.ClrType;

            if (typeof(Entity).IsAssignableFrom(clr))
            {
                modelBuilder.Entity(clr, b =>
                {
                    b.Property(nameof(Entity.Id)).ValueGeneratedNever();
                    b.Property(nameof(Entity.RowVersion)).IsRowVersion();
                });
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(clr) && entityType.BaseType is null)
            {
                SetSoftDeleteFilter(modelBuilder, clr);
            }
        }
    }

    /// <summary>Money everywhere is exact decimal (SRS 12.2). Individual entities may override.</summary>
    public static void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);

    private static void SetSoftDeleteFilter(ModelBuilder modelBuilder, Type clrType)
    {
        var parameter = Expression.Parameter(clrType, "e");
        var isDeleted = Expression.Property(Expression.Convert(parameter, typeof(ISoftDeletable)), nameof(ISoftDeletable.IsDeleted));
        var filter = Expression.Lambda(Expression.Not(isDeleted), parameter);
        modelBuilder.Entity(clrType).HasQueryFilter(SoftDeleteFilter, filter);
    }

    public const string SoftDeleteFilter = "SoftDelete";
    public const string BranchScopeFilter = "BranchScope";

    /// <summary>
    /// Hides other branches' rows from branch-scoped staff for every <see cref="IBranchScoped"/> entity. The two lambdas read the
    /// current request's scope from the DbContext instance, so one cached model serves every request. Applied as a named
    /// filter next to the soft-delete filter.
    /// </summary>
    public static void ApplyBranchScope(ModelBuilder modelBuilder, Expression<Func<bool>> enforce, Expression<Func<Guid[]>> allowed)
    {
        var contains = typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(Guid));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(IBranchScoped).IsAssignableFrom(t.ClrType) && t.BaseType is null).ToList())
        {
            var parameter = Expression.Parameter(entityType.ClrType, "e");
            var branchId = Expression.Property(Expression.Convert(parameter, typeof(IBranchScoped)), nameof(IBranchScoped.BranchId));
            var body = Expression.OrElse(
                Expression.Not(enforce.Body),
                Expression.Call(contains, allowed.Body, branchId));
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(BranchScopeFilter, Expression.Lambda(body, parameter));
        }
    }
}
