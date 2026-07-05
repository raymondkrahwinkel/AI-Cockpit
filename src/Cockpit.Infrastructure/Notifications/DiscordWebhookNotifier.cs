using System.Text;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;

namespace Cockpit.Infrastructure.Notifications;

/// <summary>
/// POSTs a needs-attention notification to a Discord webhook as <c>application/json</c> with the
/// <c>{"content":"..."}</c> body Discord expects. This is the primary "operator is away" channel
/// and must be solid: it is self-contained (a plain HTTP POST, no AI-Hub / MCP dependency), and a
/// non-2xx response or transport failure is logged rather than thrown, so a failed notification
/// never breaks the session flow that triggered it.
/// </summary>
internal sealed class DiscordWebhookNotifier(HttpClient httpClient, ILogger<DiscordWebhookNotifier> logger)
    : IWebhookNotifier, ISingletonService
{
    public async Task NotifyAsync(string webhookUrl, AttentionNotification notification, CancellationToken cancellationToken = default)
    {
        var payload = DiscordWebhookPayload.FromNotification(notification);
        using var content = new StringContent(payload.ToJson(), Encoding.UTF8, "application/json");

        try
        {
            using var response = await httpClient.PostAsync(webhookUrl, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Discord webhook POST returned {StatusCode} for notification '{Title}'.",
                    (int)response.StatusCode,
                    notification.Title);
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Discord webhook POST failed for notification '{Title}'.", notification.Title);
        }
    }
}
