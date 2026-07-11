using Avalonia.Controls;

namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// A plugin-registered provider's "add/edit profile" settings panel (#45), parallel to
/// <see cref="IPluginSettingsView"/>. Implementations take the existing config JSON (or <see langword="null"/>
/// when adding a new profile) as a constructor argument and pre-fill their fields from it.
/// </summary>
public interface IPluginProviderConfigView
{
    /// <summary>The control hosting this provider's config fields, embedded in the profile editor.</summary>
    Control View { get; }

    /// <summary>Validates the current field values and serializes them; returns <see langword="false"/> (and no JSON) when validation fails, keeping the editor open.</summary>
    bool TryGetConfigJson(out string configJson);
}
