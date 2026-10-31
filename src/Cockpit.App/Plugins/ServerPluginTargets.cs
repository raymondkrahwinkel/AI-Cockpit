using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.App.Plugins;

// AC-1547: the servers a plugin's own screen can manage, each with that plugin's host there (AC-1571's server copy). The
// plugin must be on the server in the same version: another version speaks another channel, and that is said, not guessed.
internal static class ServerPluginTargets
{
    public static async Task<IReadOnlyList<PluginServerTarget>> ForAsync(IRemoteServers servers, ICockpitUiHost local, string pluginId, string pluginName, string localVersion)
    {
        List<PluginServerTarget> targets = [];
        foreach (var server in servers.Servers)
        {
            targets.Add(await _TargetAsync(server, local, pluginId, pluginName, localVersion));
        }

        return targets;
    }

    private static async Task<PluginServerTarget> _TargetAsync(IRemoteServer server, ICockpitUiHost local, string pluginId, string pluginName, string localVersion)
    {
        if (!server.State.IsConnected)
        {
            return new PluginServerTarget(server.Name, null, $"{server.Name} is not connected.");
        }

        if (server.State.Key?.Capability != "admin" || server.Plugins is not IPluginChannelRelay relay)
        {
            return new PluginServerTarget(server.Name, null, $"This computer's key for {server.Name} is not an admin key.");
        }

        try
        {
            var installed = await server.Plugins.GetInstalledAsync();
            var plugin = installed.Select(entry => entry.Discovered).FirstOrDefault(discovered => string.Equals(discovered.Manifest.Id, pluginId, StringComparison.Ordinal));
            if (plugin is null)
            {
                return new PluginServerTarget(server.Name, null, $"{pluginName} is not installed on {server.Name}.");
            }

            if (!string.Equals(plugin.Manifest.Version, localVersion, StringComparison.Ordinal))
            {
                return new PluginServerTarget(server.Name, null, $"{server.Name} has {pluginName} {plugin.Manifest.Version}, this computer {localVersion}.");
            }

            var host = await ServerPluginSettings.OpenScreenHostAsync(local, relay, plugin.FolderId, () => ProfilesAsync(server.Profiles), () => ProjectsAsync(server.Projects), server.Name);
            return new PluginServerTarget(server.Name, host);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new PluginServerTarget(server.Name, null, $"{server.Name} did not answer: {exception.Message}");
        }
    }

    // AC-1571: the server's profiles as a plugin's host gives them, from the Admin API's own list. No config directory:
    // that path stays on the server.
    public static async Task<IReadOnlyList<PluginProfileInfo>> ProfilesAsync(IServerProfiles profiles) =>
        [.. (await profiles.ListAsync()).Select(profile => new PluginProfileInfo(profile.Label, profile.Provider, "")
        {
            ModelSuggestions = _Known(profile, WellKnownPluginSessionOptions.Model),
            EffortSuggestions = _Known(profile, WellKnownPluginSessionOptions.Effort),
        })];

    // AC-1737: the server's projects, read again from its API so a project added there since shows up.
    public static async Task<IReadOnlyList<PluginProjectInfo>> ProjectsAsync(IServerProjects projects)
    {
        await projects.LoadAsync();
        return [.. projects.Current.Settings.Projects.Select(project => new PluginProjectInfo(project.Id, project.Name))];
    }

    private static IReadOnlyList<string> _Known(RemoteProfile profile, string option) =>
        profile.KnownValues?.GetValueOrDefault(option) is { } values ? [.. values.Select(value => value.Value)] : [];
}
