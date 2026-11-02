namespace Juvo.Application.Abstractions;

/// <summary>Sends SMS. The default implementation is a sandbox that writes to the notification log (no provider needed).</summary>
public interface ISmsSender
{
    Task SendAsync(string mobile, string template, string body, CancellationToken cancellationToken = default);
}

/// <summary>Sends email. The default implementation is a sandbox that writes to the notification log.</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string template, string subject, string body, CancellationToken cancellationToken = default);
}

/// <summary>Writes a request that was refused (wrong branch, missing permission) to the audit log so it can be reviewed.</summary>
public interface IAccessDeniedAuditor
{
    Task RecordAsync(string entity, string detail, CancellationToken cancellationToken = default);
}
