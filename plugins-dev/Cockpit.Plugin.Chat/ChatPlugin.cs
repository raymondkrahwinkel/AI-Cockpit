using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Chat;

/// <summary>
/// Slack and Discord, as steps a flow can take (#69). The cockpit already tells <em>you</em> things — a toast on your
/// screen, a Discord message when you are away from it. This is the other direction: telling other people, on purpose,
/// as part of the work.
/// </summary>
public sealed class ChatPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "chat",
        DisplayName: "Chat",
        Version: "1.0.0",
        Author: "Cockpit",
        Description: "Send a message to Slack or Discord from a workflow. Channels are named in the plugin's settings — an incoming webhook per channel — so a flow says \"tell releases\" rather than carrying a URL around: a webhook is a credential, and a flow's own settings are the last place one should live. A message the service refused fails the step, because a notification nobody received is worse than an error nobody missed.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new ChatSettings(host.Storage);

        host.AddSettings(() => new ChatSettingsControl(settings));

        foreach (var step in ChatWorkflowSteps.All(settings))
        {
            host.AddWorkflowStep(step);
        }
    }

    public void Dispose()
    {
    }
}
