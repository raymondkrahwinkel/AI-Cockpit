namespace Cockpit.Plugin.YouTrack;

// The issue a session pane is working on, with the instance it came from — an issue id alone is not enough to
// go back to YouTrack for its status when several instances are configured (#48).
internal sealed record LinkedIssue(YouTrackInstance Instance, YouTrackIssue Issue);
