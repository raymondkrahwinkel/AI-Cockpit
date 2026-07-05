using Cockpit.Core.Notifications;

namespace Cockpit.Core.Abstractions.Notifications;

/// <summary>POSTs a needs-attention notification to a Discord webhook for the away operator.</summary>
public interface IWebhookNotifier
{
    Task NotifyAsync(string webhookUrl, AttentionNotification notification, CancellationToken cancellationToken = default);
}
