namespace Cockpit.Plugin.YouTrack;

// A project configured on a YouTrack instance, as returned by the admin API (#48): its `ShortName`
// (the issue-id prefix, e.g. "EJT", used as the server-side `project:` query tag) and its full
// `Name` for a human-readable dropdown label. `Name` may be empty when the API omits it.
public sealed record YouTrackProject(string ShortName, string Name);
