namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// A project configured on a YouTrack instance, as returned by the admin API (#48): its <see cref="ShortName"/>
/// (the issue-id prefix, e.g. "EJT", used as the server-side <c>project:</c> query tag) and its full
/// <see cref="Name"/> for a human-readable dropdown label. <see cref="Name"/> may be empty when the API omits it.
/// </summary>
public sealed record YouTrackProject(string ShortName, string Name);
