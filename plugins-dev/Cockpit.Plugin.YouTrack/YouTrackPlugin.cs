using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Plugin #42, mirroring the GitHub Pull Requests plugin (#41) for YouTrack issues. Unlike the GitHub
/// plugins, YouTrack has no local CLI equivalent to <c>gh</c>, so this plugin is HTTP-only: a permanent
/// token against a configured instance and project (see settings) — no CLI-vs-HTTP toggle. It registers a
/// settings view (opened from the plugin manager's gear) and an inline side-menu section, always visible
/// under the session list, showing up to 5 open issues for the configured project plus a button opening a
/// dialog with every open issue. Clicking an issue — in the section or the dialog — injects the rendered
/// template into the active session so the agent picks it up, falling back to the clipboard when there is no
/// active session. Its settings live in the host's per-plugin storage, so <see cref="ConfigureServices"/> is
/// empty.
/// </summary>
public sealed class YouTrackPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "youtrack",
        DisplayName: "YouTrack",
        Version: "1.0.0",
        Author: "Cockpit",
        Description: "Shows up to 5 open YouTrack issues inline under the session list for a configured project (over HTTP with a permanent token), plus a dialog listing every open issue. Click one to drop a prompt asking the agent to work on it. The prompt template is editable in settings.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var settings = new YouTrackSettings(host.Storage);

        host.AddSettings(() => new YouTrackSettingsControl(settings));
        host.AddSideMenuSection("YouTrack", () => new YouTrackSideSectionControl(settings, host));
    }

    public void Dispose()
    {
    }
}
