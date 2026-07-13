using System.Text;

namespace Cockpit.Plugin.Chat;

/// <summary>
/// Posts to an incoming webhook. One shared client, because both services are one host each and a client per call is
/// how a desktop app runs out of sockets.
/// <para>
/// A refusal is raised, never swallowed. A notification nobody received is worse than an error nobody missed: the
/// whole point of telling a flow to say something is that it was said.
/// </para>
/// </summary>
internal sealed class ChatClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task SendAsync(ChatChannel channel, string message, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(channel.WebhookUrl.Trim(), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"'{channel.Name}' has no https webhook. Put its incoming-webhook URL in the plugin's settings.");
        }

        using var content = new StringContent(ChatMessage.Body(channel.Service, message), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(url, content, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var said = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();

        throw new InvalidOperationException(
            said.Length > 0
                ? $"{channel.Service} refused the message ({(int)response.StatusCode}): {said}"
                : $"{channel.Service} refused the message ({(int)response.StatusCode} {response.ReasonPhrase}).");
    }
}
