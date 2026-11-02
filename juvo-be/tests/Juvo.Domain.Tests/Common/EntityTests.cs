using Juvo.Domain.Common;

namespace Juvo.Domain.Tests.Common;

public class EntityTests
{
    private sealed class Sample : SoftDeletableEntity
    {
    }

    private sealed record SampleEvent(DateTimeOffset At) : DomainEvent(At);

    private sealed class SampleRoot : AggregateRoot
    {
        public void Do(DateTimeOffset at) => Raise(new SampleEvent(at));
    }

    [Fact]
    public void Ids_are_unique_and_time_ordered()
    {
        var first = new Sample();
        var second = new Sample();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(7, first.Id.Version);
    }

    [Fact]
    public void Stamps_record_time_and_user()
    {
        var now = DateTimeOffset.UtcNow;
        var user = Guid.NewGuid();
        var entity = new Sample();

        entity.StampCreated(now, user);
        entity.StampUpdated(now.AddMinutes(1), user);

        Assert.Equal(now, entity.CreatedAt);
        Assert.Equal(user, entity.CreatedBy);
        Assert.Equal(now.AddMinutes(1), entity.UpdatedAt);
    }

    [Fact]
    public void Soft_delete_and_restore()
    {
        var entity = new Sample();
        var now = DateTimeOffset.UtcNow;

        entity.MarkDeleted(now, null);
        Assert.True(entity.IsDeleted);
        Assert.Equal(now, entity.DeletedAt);

        entity.Restore();
        Assert.False(entity.IsDeleted);
        Assert.Null(entity.DeletedAt);
    }

    [Fact]
    public void Aggregate_collects_and_clears_domain_events()
    {
        var root = new SampleRoot();

        root.Do(DateTimeOffset.UtcNow);

        Assert.Single(root.DomainEvents);
        root.ClearDomainEvents();
        Assert.Empty(root.DomainEvents);
    }
}
