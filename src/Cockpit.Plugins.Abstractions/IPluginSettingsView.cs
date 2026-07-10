namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// Optional interface a plugin's settings view (the control passed to <see cref="ICockpitHost.AddSettings"/>)
/// can implement so the host's settings dialog provides a standard Save/Close footer for it (#14): the host
/// shows a Save button that calls <see cref="Save"/> and closes the dialog when it returns true. A settings
/// view that applies changes live and needs no explicit save can skip this — it just gets a Close button.
/// </summary>
public interface IPluginSettingsView
{
    /// <summary>Persist the settings. Return true to close the dialog, or false to keep it open (e.g. validation failed).</summary>
    bool Save();
}
