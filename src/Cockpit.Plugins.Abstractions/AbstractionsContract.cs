namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// The major version of this plugin contract. The host loads a plugin only when the
/// <c>abstractionsVersion</c> in its manifest matches <see cref="Version"/>.
/// </summary>
/// <remarks>
/// The contract grows additively within a major (new members as default interface methods on
/// <see cref="ICockpitHost"/>); a breaking change bumps this. <strong>2</strong> replaced
/// <see cref="IPluginSettingsView"/>'s <c>bool Save()</c> with <see cref="IPluginSettingsView.TryStage"/>.
/// </remarks>
public static class AbstractionsContract
{
    public const int Version = 2;
}
