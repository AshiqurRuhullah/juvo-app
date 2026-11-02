using Juvo.Domain.Common;

namespace Juvo.Domain.Platform;

public enum JobStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
}

/// <summary>One execution attempt of a background job (outbox delivery, scheduled task). Feeds the job dashboard (FR-AF02).</summary>
public sealed class JobLog : Entity, INotAudited
{
    private JobLog()
    {
    }

    public JobLog(string jobName, string? reference, int attempt, string? correlationId, DateTimeOffset startedAt)
    {
        JobName = jobName;
        Reference = reference;
        Attempt = attempt;
        CorrelationId = correlationId;
        StartedAt = startedAt;
    }

    public string JobName { get; private set; } = string.Empty;

    /// <summary>Id of the thing processed (for example the outbox message id).</summary>
    public string? Reference { get; private set; }

    public int Attempt { get; private set; }

    public JobStatus Status { get; private set; } = JobStatus.Running;

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public string? Error { get; private set; }

    public string? CorrelationId { get; private set; }

    public void Succeed(DateTimeOffset now)
    {
        Status = JobStatus.Succeeded;
        FinishedAt = now;
    }

    public void Fail(DateTimeOffset now, string error)
    {
        Status = JobStatus.Failed;
        FinishedAt = now;
        Error = error.Length > 2000 ? error[..2000] : error;
    }
}
