namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// The major version of this plugin contract. A plugin's manifest declares the
/// <c>abstractionsVersion</c> it was built against; the host loads it only when that major matches
/// <see cref="Version"/>. The contract grows additively within a major (new members as default
/// interface methods on <see cref="ICockpitHost"/>); a breaking change bumps this.
/// </summary>
public static class AbstractionsContract
{
    public const int Version = 1;
}
