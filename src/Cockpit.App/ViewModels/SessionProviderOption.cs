using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

// A selectable provider in the Manage-profiles dropdown (#26): a display label paired with its `SessionProvider`.
public sealed record SessionProviderOption(string Label, SessionProvider Value, string? PluginProviderId = null);
