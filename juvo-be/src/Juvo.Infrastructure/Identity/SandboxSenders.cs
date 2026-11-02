using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Juvo.Application.Abstractions;
using Juvo.Domain.Notifications;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Identity;

/// <summary>Notification behaviour. Section: <c>Notifications</c>.</summary>
public sealed class NotificationOptions
{
    public const string SectionName = "Notifications";

    /// <summary>
    /// Keep one-time codes readable in the sandbox log. On in Development and tests so the flow can be tried without a phone;
    /// off elsewhere, where codes are replaced by asterisks in the stored message.
    /// </summary>
    public bool StoreOtpPlaintext { get; set; }

    [Required]
    public string Provider { get; set; } = "sandbox";
}

/// <summary>Sandbox SMS: no provider is called; the message is written to the notification log so the platform runs end to end.</summary>
internal sealed partial class SandboxSmsSender(
    JuvoDbContext db, TimeProvider timeProvider, ICurrentUser currentUser, IOptions<NotificationOptions> options,
    ILogger<SandboxSmsSender> logger) : ISmsSender
{
    [GeneratedRegex(@"\b\d{4,8}\b")]
    private static partial Regex Digits();

    public async Task SendAsync(string mobile, string template, string body, CancellationToken cancellationToken = default)
    {
        var stored = options.Value.StoreOtpPlaintext ? body : Digits().Replace(body, "******");
        db.NotificationLogs.Add(new NotificationLog(NotificationChannel.Sms, mobile, template, null, stored, NotificationStatus.Sent,
            "sandbox", currentUser.CorrelationId, timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Sandbox SMS to {Mobile} ({Template}) written to the notification log.", mobile, template);
    }
}

/// <summary>Sandbox email: the message is stored in the notification log, viewable by admins. Password-reset links are kept.</summary>
internal sealed class SandboxEmailSender(
    JuvoDbContext db, TimeProvider timeProvider, ICurrentUser currentUser, ILogger<SandboxEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string template, string subject, string body, CancellationToken cancellationToken = default)
    {
        db.NotificationLogs.Add(new NotificationLog(NotificationChannel.Email, to, template, subject, body, NotificationStatus.Sent,
            "sandbox", currentUser.CorrelationId, timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Sandbox email to {To} ({Template}) written to the notification log.", to, template);
    }
}

/// <summary>
/// Writes "access denied" audit rows in a fresh scope, so the record survives even when the request's own database context
/// is in a failed state (for example after a blocked write).
/// </summary>
internal sealed class AccessDeniedAuditor(IServiceScopeFactory scopeFactory, ICurrentUser currentUser, TimeProvider timeProvider) : IAccessDeniedAuditor
{
    public async Task RecordAsync(string entity, string detail, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        db.AuditLogs.Add(new AuditLog(currentUser.UserId, currentUser.BranchId, entity, currentUser.UserId?.ToString() ?? "anonymous",
            AuditAction.AccessDenied, null, detail.Length > 2000 ? detail[..2000] : detail, currentUser.CorrelationId,
            currentUser.IpAddress, timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
    }
}
