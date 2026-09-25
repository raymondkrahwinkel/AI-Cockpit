namespace Cockpit.Plugin.YouTrack;

// The half of YouTrackProjectField both parts need (AC-1397): the UI part preselects the linked cockpit project in a
// new session by it, the backend part registers and reads the field by it.
internal static partial class YouTrackProjectField
{
    // What the link is stored under on the project. Never change it: already-linked projects are keyed by it.
    public const string Key = "youtrack.project";
}
