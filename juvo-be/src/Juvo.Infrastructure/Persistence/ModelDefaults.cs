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
        modelBuilder.Entity(clrType).HasQueryFilter(filter);
    }
}
