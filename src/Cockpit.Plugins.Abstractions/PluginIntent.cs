namespace Cockpit.Plugins.Abstractions;

/// <summary>
/// A call one plugin makes on another (AC-95), addressed by the target plugin's manifest id — the plugin equivalent
/// of an Android intent. It names who is calling, who is being called and what is being asked, carries a
/// string→string payload, and the handler answers with a string→string result. There is deliberately no shared
/// contract type between the two plugins: the caller names the target by its id and the action by an agreed string,
/// the same loose coupling the workflow steps use, so a plugin can call one it was never compiled against — the
/// tracker's "Start in Autopilot" reaches the Autopilot plugin without either referencing the other.
/// <para>
/// <see cref="CallerPluginId"/> is stamped by the host from the calling plugin's own identity, never taken from
/// anything the caller composes — a plugin cannot place an intent under another's name (the same rule the consent
/// gate uses for <c>ConsentRequest.Source</c>).
/// </para>
/// <para>
/// <see cref="TargetPluginId"/> and <see cref="Action"/> are matched <em>case-sensitively</em> (ordinal): the sender
/// and the handler must agree on the exact strings. A mismatch dispatches to nobody and comes back the same way an
/// uninstalled target does (a null result), so a caller that offers this as an action gates it on
/// <c>ICockpitHost.CanSendIntent</c> first — a wrong-cased action then shows up as a missing menu item at
/// integration time rather than a silent no-op later.
/// </para>
/// </summary>
public sealed record PluginIntent(
    string CallerPluginId,
    string TargetPluginId,
    string Action,
    IReadOnlyDictionary<string, string> Data);
