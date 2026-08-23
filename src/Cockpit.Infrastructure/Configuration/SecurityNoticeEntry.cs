namespace Cockpit.Infrastructure.Configuration;

// AC-41: `SecurityNotice` — what the operator has been told and dismissed, currently just the awareness
// banner. Its own section, apart from the crypto entry, since it must stay readable while encryption is
// off and carries no secret, only field locations. A hand-edit clearing it just re-shows the banner once.
internal sealed class SecurityNoticeEntry
{
    // Review #7: credential field paths in the clear when the banner was dismissed, sorted. Re-nags only
    // for a path not in this set — a genuinely new credential. Null/empty means never dismissed. These
    // are field locations, not values, kept in an array so the secret walker never mistakes them for one.
    public List<string>? DismissedPaths { get; set; }
}
