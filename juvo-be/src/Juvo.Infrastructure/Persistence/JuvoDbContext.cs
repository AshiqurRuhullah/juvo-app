using Juvo.Application.Abstractions;
using Juvo.Domain.Branches;
using Juvo.Domain.Catalog;
using Juvo.Domain.Customers;
using Juvo.Domain.Identity;
using Juvo.Domain.Notifications;
using Juvo.Domain.Platform;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Infrastructure.Persistence;

/// <summary>
/// The platform database. Staff accounts use ASP.NET Core Identity (no Identity roles: roles, permissions and branch
/// assignments are our own tables). Branch-scoped entities are filtered by the current request's branch scope.
/// </summary>
public sealed class JuvoDbContext : IdentityUserContext<StaffUser, Guid>, IApplicationDbContext
{
    private readonly IBranchContext _branchContext;

    public JuvoDbContext(DbContextOptions<JuvoDbContext> options, IBranchContext? branchContext = null)
        : base(options)
    {
        _branchContext = branchContext ?? UnscopedBranchContext.Instance;
    }

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<Permission> Permissions => Set<Permission>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<UserBranchRole> UserBranchRoles => Set<UserBranchRole>();

    public DbSet<Branch> Branches => Set<Branch>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<SignInHistory> SignInHistory => Set<SignInHistory>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();

    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Brand> Brands => Set<Brand>();

    public DbSet<SizeChart> SizeCharts => Set<SizeChart>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    public DbSet<ProductImage> ProductImages => Set<ProductImage>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<JobLog> JobLogs => Set<JobLog>();

    // Read by the branch-scope query filter on every query (see ModelDefaults.ApplyBranchScope).
    private bool EnforceBranchScope => _branchContext.EnforceScope;

    private Guid[] AllowedBranches => [.. _branchContext.AllowedBranchIds];

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<StaffUser>().ToTable("StaffUsers");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("StaffUserClaims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("StaffUserLogins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("StaffUserTokens");

        builder.ApplyConfigurationsFromAssembly(typeof(JuvoDbContext).Assembly);
        ModelDefaults.Apply(builder);
        ModelDefaults.ApplyBranchScope(builder, () => EnforceBranchScope, () => AllowedBranches);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        ModelDefaults.ConfigureConventions(configurationBuilder);
    }
}
