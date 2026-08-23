namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// A call one plugin makes on another (AC-95), addressed by the target plugin's manifest id — the plugin
/// equivalent of an Android intent. Carries a string→string payload; the handler answers with a string→string
/// result.
/// </summary>
/// <remarks>
/// There is deliberately no shared contract type between the two plugins — the caller names the target by id
/// and the action by an agreed string, so a plugin can call one it was never compiled against.
/// <see cref="CallerPluginId"/> is stamped by the host from the calling plugin's own identity. Match
/// <see cref="TargetPluginId"/> and <see cref="Action"/> case-sensitively — a mismatch dispatches to nobody and
/// comes back like an uninstalled target; gate the caller on <c>ICockpitHost.CanSendIntent</c> first.
/// </remarks>
public sealed record PluginIntent(
    string CallerPluginId,
    string TargetPluginId,
    string Action,
    IReadOnlyDictionary<string, string> Data);
