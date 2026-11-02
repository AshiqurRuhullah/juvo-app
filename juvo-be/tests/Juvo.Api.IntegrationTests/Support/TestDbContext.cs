using Juvo.Application.Abstractions;
using Juvo.Domain.Common;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;
using Juvo.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Juvo.Api.IntegrationTests.Support;

public sealed record WidgetRenamed(DateTimeOffset At, Guid WidgetId, string NewName) : DomainEvent(At);

/// <summary>Sample aggregate: soft-deletable, raises events, one property excluded from the audit log.</summary>
public sealed class Widget : AggregateRoot
{
    private Widget()
    {
    }

    public Widget(string name, decimal price, string secret)
    {
        Name = name;
        Price = price;
        Secret = secret;
    }

    public string Name { get; private set; } = string.Empty;

    public decimal Price { get; private set; }

    [AuditIgnore]
    public string Secret { get; private set; } = string.Empty;

    public void Rename(string name, DateTimeOffset at)
    {
        Name = name;
        Raise(new WidgetRenamed(at, Id, name));
    }

    public void Reprice(decimal price) => Price = price;
}

/// <summary>Sample append-only table (like stock movements or payments).</summary>
public sealed class LedgerEntry : Entity, IAppendOnly
{
    private LedgerEntry()
    {
    }

    public LedgerEntry(decimal amount) => Amount = amount;

    public decimal Amount { get; private set; }

    public void Change(decimal amount) => Amount = amount;
}

/// <summary>Sample branch-scoped data (like orders, stock, expenses).</summary>
public sealed class BranchNote : Entity, IBranchScoped
{
    private BranchNote()
    {
    }

    public BranchNote(Guid branchId, string text)
    {
        BranchId = branchId;
        Text = text;
    }

    public Guid BranchId { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public void MoveTo(Guid branchId) => BranchId = branchId;
}

/// <summary>Settable branch scope for tests.</summary>
public sealed class FakeBranchContext : IBranchContext
{
    public Guid? ActiveBranchId { get; set; }

    public bool IsAllBranches { get; set; }

    public bool EnforceScope { get; set; }

    public IReadOnlyCollection<Guid> AllowedBranchIds { get; set; } = [];

    public static FakeBranchContext Scoped(params Guid[] branches) =>
        new() { EnforceScope = true, AllowedBranchIds = branches, ActiveBranchId = branches.Length == 1 ? branches[0] : null };

    public static FakeBranchContext Unscoped() => new() { EnforceScope = false, IsAllBranches = true };
}

/// <summary>Real platform tables plus two sample entities, using the same model rules and interceptors as production.</summary>
public sealed class TestDbContext(DbContextOptions<TestDbContext> options, IBranchContext? branchContext = null) : DbContext(options)
{
    private readonly IBranchContext _branchContext = branchContext ?? UnscopedBranchContext.Instance;

    private bool EnforceBranchScope => _branchContext.EnforceScope;

    private Guid[] AllowedBranches => [.. _branchContext.AllowedBranchIds];

    public DbSet<BranchNote> BranchNotes => Set<BranchNote>();

    public DbSet<Widget> Widgets => Set<Widget>();

    public DbSet<LedgerEntry> Ledger => Set<LedgerEntry>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditLog>().ToTable("AuditLogs");
        modelBuilder.Entity<OutboxMessage>().ToTable("OutboxMessages");
        modelBuilder.Entity<Widget>().ToTable("Widgets").Property(w => w.Name).HasMaxLength(100);
        modelBuilder.Entity<LedgerEntry>().ToTable("Ledger");
        modelBuilder.Entity<BranchNote>().ToTable("BranchNotes");
        ModelDefaults.Apply(modelBuilder);
        ModelDefaults.ApplyBranchScope(modelBuilder, () => EnforceBranchScope, () => AllowedBranches);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        ModelDefaults.ConfigureConventions(configurationBuilder);
}

public sealed class FakeCurrentUser : ICurrentUser
{
    public bool IsAuthenticated => UserId is not null;

    public Guid? UserId { get; set; } = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public string? UserName => "tester";

    public Guid? BranchId { get; set; } = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public string? IpAddress => "10.0.0.1";

    public string? UserAgent => "tests";

    public Guid? SessionId => null;

    public bool IsLimitedToken => false;

    public string? SubjectType => "staff";

    public string? CorrelationId => "corr-test-1";
}

/// <summary>A throw-away database with the sample tables, the production interceptors and a controllable clock.</summary>
public sealed class TestDbFixture : IAsyncLifetime
{
    private readonly string _connectionString = TestDatabase.NewConnectionString();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

    public FakeCurrentUser User { get; } = new();

    public TestDbContext NewContext(IBranchContext? branchContext = null)
    {
        var scope = branchContext ?? FakeBranchContext.Unscoped();
        var builder = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlServer(_connectionString)
            .AddInterceptors(
                new BranchWriteGuardInterceptor(scope),
                new DomainEventsOutboxInterceptor(Time, User),
                new AuditSaveChangesInterceptor(Time, User));
        return new TestDbContext(builder.Options, scope);
    }

    public async ValueTask InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await TestDatabase.DropAsync(_connectionString);
        GC.SuppressFinalize(this);
    }
}
