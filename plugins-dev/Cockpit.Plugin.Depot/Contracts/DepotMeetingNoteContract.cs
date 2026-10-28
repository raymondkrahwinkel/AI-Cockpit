using System.Globalization;

namespace Cockpit.Plugin.Depot.Contracts;

// AC-1579: the "New meeting note" dialog between the UI part and the backend part, which alone talks to Depot's /cockpit
// routes. Cockpit sends choices only; the note's Markdown, its template and the invitations all come from Depot.
internal static class DepotMeetingNoteChannel
{
    // Payload: DepotMeetingNoteRequest. Answers a DepotBrowserAnswer of DepotMeetingNoteOptions: the projects you are Editor of.
    public const string Options = "meeting-note-options";

    // Payload: DepotMeetingNoteRequest with Project. Answers a DepotBrowserAnswer of DepotMeetingNoteProjectInfo.
    public const string Project = "meeting-note-project";

    // Payload: DepotMeetingNoteRequest with Project, Folder and Topic. Answers a DepotBrowserAnswer of DepotMeetingNoteCheck.
    public const string Check = "meeting-note-check";

    // Payload: DepotMeetingNoteRequest with Project, Topic, Folder, Template and Attendees. Answers a DepotBrowserAnswer of
    // DepotMeetingNoteCreated.
    public const string Create = "meeting-note-create";

    // Payload: DepotMeetingNoteRequest with Project and Path. Answers a DepotBrowserAnswer of the note's absolute link.
    public const string Link = "meeting-note-link";
}

// One request shape for every action; each action reads the fields it needs. There is deliberately no content field:
// what a note says is Depot's to write.
internal sealed record DepotMeetingNoteRequest(
    string ConnectionId, string? Project = null, string? Topic = null, string? Folder = null, string? Template = null,
    IReadOnlyList<string>? Attendees = null, string? Path = null);

internal sealed record DepotMeetingNoteProject(
    string Address, string Name, string Organization, string Role, string? Folder, bool Allowed, string? PlanHref);

// LeftOut counts the projects where you are a Viewer; Recent lists addresses, most recent first.
internal sealed record DepotMeetingNoteOptions(
    IReadOnlyList<DepotMeetingNoteProject> Projects, int LeftOut, IReadOnlyList<string> Recent);

// Role is null for a member without access to the project.
internal sealed record DepotMeetingNotePerson(string Id, string Name, string? Role);

internal sealed record DepotMeetingNoteProjectInfo(IReadOnlyList<string> Folders, IReadOnlyList<DepotMeetingNotePerson> People);

internal sealed record DepotMeetingNoteCheck(bool Exists, string? By, DateTimeOffset? At, int Live);

// Existing is true when the name was taken: Path is then the note that is already there.
internal sealed record DepotMeetingNoteCreated(string Path, bool Existing);

internal static class DepotMeetingNoteTemplate
{
    public const string Meeting = "meeting";
    public const string Empty = "empty";
}

// "<folder>/<date> <topic>.md", the name Depot's own dialog proposes; the topic loses what a path cannot hold.
internal static class DepotMeetingNoteName
{
    public const string DefaultFolder = "Meetings";

    public static string Build(string? folder, string topic, DateOnly date)
    {
        var clean = string.Concat(topic.Trim().Where(character => !char.IsControl(character) && "/\\:*?\"<>|".IndexOf(character) < 0)).Trim();
        var name = $"{date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} {clean}.md";
        return string.IsNullOrEmpty(folder) ? name : $"{folder.Trim('/')}/{name}";
    }
}
