namespace Cockpit.Infrastructure.Sessions;

// AC-1473: every load-change-save of the profile list in this process holds this lock (the admin API's profile
// routes, the delegation tools' describe and add), so two edits at once cannot save over each other.
internal static class ProfileEdits
{
    public static readonly SemaphoreSlim Gate = new(1, 1);
}
