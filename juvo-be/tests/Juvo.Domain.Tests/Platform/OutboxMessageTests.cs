using Juvo.Domain.Platform;

namespace Juvo.Domain.Tests.Platform;

public class OutboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Base = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Max = TimeSpan.FromHours(1);

    [Fact]
    public void New_message_is_pending_and_due_now()
    {
        var message = new OutboxMessage("order.placed", "{}", Now, "corr-1");

        Assert.Equal(OutboxStatus.Pending, message.Status);
        Assert.Equal(Now, message.NextAttemptAt);
        Assert.Equal(0, message.AttemptCount);
    }

    [Fact]
    public void Failure_schedules_exponential_backoff()
    {
        var message = new OutboxMessage("t", "{}", Now, null);

        message.MarkFailed(Now, "boom", 8, Base, Max);
        Assert.Equal(Now.AddSeconds(5), message.NextAttemptAt);

        message.MarkFailed(Now, "boom", 8, Base, Max);
        Assert.Equal(Now.AddSeconds(10), message.NextAttemptAt);

        message.MarkFailed(Now, "boom", 8, Base, Max);
        Assert.Equal(Now.AddSeconds(20), message.NextAttemptAt);
        Assert.Equal(OutboxStatus.Pending, message.Status);
    }

    [Fact]
    public void Backoff_is_capped()
    {
        var message = new OutboxMessage("t", "{}", Now, null);
        for (var i = 0; i < 11; i++)
        {
            message.MarkFailed(Now, "boom", 50, Base, Max);
        }

        Assert.Equal(Now.Add(Max), message.NextAttemptAt);
    }

    [Fact]
    public void Message_fails_for_good_after_max_attempts_and_can_be_requeued()
    {
        var message = new OutboxMessage("t", "{}", Now, null);
        for (var i = 0; i < 3; i++)
        {
            message.MarkFailed(Now, "boom", 3, Base, Max);
        }

        Assert.Equal(OutboxStatus.Failed, message.Status);
        Assert.Equal("boom", message.LastError);

        message.Requeue(Now.AddMinutes(1));

        Assert.Equal(OutboxStatus.Pending, message.Status);
        Assert.Equal(0, message.AttemptCount);
        Assert.Equal(Now.AddMinutes(1), message.NextAttemptAt);
    }

    [Fact]
    public void Success_clears_the_error()
    {
        var message = new OutboxMessage("t", "{}", Now, null);
        message.MarkFailed(Now, "boom", 8, Base, Max);

        message.MarkSucceeded(Now.AddSeconds(6));

        Assert.Equal(OutboxStatus.Succeeded, message.Status);
        Assert.Null(message.LastError);
        Assert.Equal(Now.AddSeconds(6), message.ProcessedAt);
    }
}
