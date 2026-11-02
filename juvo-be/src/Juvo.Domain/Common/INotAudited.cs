namespace Juvo.Domain.Common;

/// <summary>
/// Marker for log-like or secret-bearing tables that must not be copied into the audit log: the audit log itself, outbox and job
/// logs, refresh tokens, sign-in history, OTP codes and notification logs. They are records of events already.
/// </summary>
public interface INotAudited
{
}
