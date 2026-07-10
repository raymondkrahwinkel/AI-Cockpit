using Cockpit.Core.Profiles;

namespace Cockpit.App.ViewModels;

/// <summary>A selectable provider in the Manage-profiles dropdown (#26): a display label paired with its <see cref="SessionProvider"/>.</summary>
public sealed record SessionProviderOption(string Label, SessionProvider Value);
