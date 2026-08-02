using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

// A selectable provider in the Manage-profiles dropdown (#26): a display label paired with its
// `SessionProvider`. For a plugin-registered provider (#45), `Value` is always
// `SessionProvider.Plugin` and `PluginProviderId` disambiguates which one — several
// plugins (or one plugin registering several providers) can each contribute an option sharing that same
// enum value.
public sealed record SessionProviderOption(string Label, SessionProvider Value, string? PluginProviderId = null);
