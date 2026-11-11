using Juvo.Application.Notifications;
using Juvo.Domain.Notifications;
using Microsoft.Extensions.Logging;

namespace Juvo.Infrastructure.Notifications;

/// <summary>
/// Sandbox sender for one channel (SMS, email or WhatsApp): no provider is called. The notification log row, which already holds the rendered message, is the record, so the whole
/// platform runs end to end without any provider account. A real sender (Step 14) is registered after this one and takes over its channel.
/// </summary>
internal sealed class SandboxNotificationSender(NotificationChannel channel, ILogger<SandboxNotificationSender> logger) : INotificationSender
{
    public NotificationChannel Channel => channel;

    public Task<SendOutcome> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Sandbox {Channel} to {Recipient} ({Template}) recorded in the notification log.", channel, message.Recipient, message.Template);
        return Task.FromResult(SendOutcome.Ok("sandbox"));
    }
}
