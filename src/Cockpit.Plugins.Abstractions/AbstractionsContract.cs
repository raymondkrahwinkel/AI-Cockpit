namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// The major version of this plugin contract. A plugin's manifest declares the
/// <c>abstractionsVersion</c> it was built against; the host loads it only when that major matches
/// <see cref="Version"/>. The contract grows additively within a major (new members as default
/// interface methods on <see cref="ICockpitHost"/>); a breaking change bumps this.
/// <para>
/// <strong>2</strong> — <see cref="IPluginSettingsView"/> no longer persists its own settings (AC-1003): the
/// single <c>bool Save()</c> became <see cref="IPluginSettingsView.TryStage"/>, which validates and hands the
/// host the write. A plugin built against 1 does not implement the new member, so its settings control would
/// fail to load the moment the operator opened its gear; refusing the plugin outright says so first.
/// </para>
/// </summary>
public static class AbstractionsContract
{
    public const int Version = 2;
}
