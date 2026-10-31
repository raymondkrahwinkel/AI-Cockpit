using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugins.Shared;

internal sealed record TtyChildSource(PluginBackgroundTask Task, string Path, string OwnerId, string WorkingDirectory);
