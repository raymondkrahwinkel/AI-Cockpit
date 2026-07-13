namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Something a plugin can do <em>to one session</em>, offered from that session's own header (#: session actions).
/// <para>
/// It exists because the alternative was a button each. Two issue trackers meant two "Track an issue" buttons sitting
/// in every session's header, both asking the same question and both taking up room whether or not anyone would ever
/// answer them. One menu holds them all, and a plugin that has nothing to show shows nothing.
/// </para>
/// </summary>
/// <param name="Title">What it says in the menu: "Track a YouTrack issue…".</param>
/// <param name="Icon">A single character or emoji, or empty.</param>
/// <param name="Invoke">Runs it, for the session it was opened from — on the UI thread.</param>
public sealed record PluginSessionAction(string Title, string Icon, Action<IPluginSessionContext> Invoke);
