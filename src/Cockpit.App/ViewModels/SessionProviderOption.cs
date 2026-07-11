using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>
/// A selectable provider in the Manage-profiles dropdown (#26): a display label paired with its
/// <see cref="SessionProvider"/>. For a plugin-registered provider (#45), <see cref="Value"/> is always
/// <see cref="SessionProvider.Plugin"/> and <see cref="PluginProviderId"/> disambiguates which one — several
/// plugins (or one plugin registering several providers) can each contribute an option sharing that same
/// enum value.
/// </summary>
public sealed record SessionProviderOption(string Label, SessionProvider Value, string? PluginProviderId = null);
