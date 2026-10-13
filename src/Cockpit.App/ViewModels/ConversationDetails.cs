using System.Globalization;
using CommunityToolkit.Mvvm.Input;

namespace Cockpit.App.ViewModels;

// AC-1533: ⋯ → Details in the Simple stand — where a conversation works, what it may do, what it has cost and what it
// runs on, in plain words, plus Open folder, Rename and Start over. Read once as the popup opens.
public sealed partial class ConversationDetails(SessionPanelViewModel session, string? project, bool consentBypass, bool canRename, Func<Task>? startOver)
{
    public static ConversationDetails For(SessionPanelViewModel session, string? project) =>
        new(session, project, consentBypass: false, canRename: true, session.StartOver);

    public string? Project => project;

    public string Folder => session.WorkingDirectory ?? "Not known yet";

    public string AllowedTo => Allowed(session.DetailsPermissionMode, consentBypass);

    public string Cost => session.DetailsCostUsd is { } usd ? $"${usd.ToString("0.00", CultureInfo.InvariantCulture)} so far" : "Not measured";

    public string Model => session.DetailsModel is { Length: > 0 } model ? model : "Not known";

    public bool CanRename => canRename;

    public bool CanStartOver => startOver is not null;

    public static string Allowed(string? mode, bool consentBypass) => (mode switch
    {
        null or "" => "Not known.",
        "default" => "Asks before changing files or running commands.",
        "acceptEdits" => "Reads and changes files in this folder without asking first; asks before running commands.",
        "plan" => "Only reads and plans; changes nothing.",
        "bypassPermissions" => "Does everything without asking first.",
        _ => $"({mode})",
    }) + (consentBypass ? " Some actions you allowed in Settings also run without asking you first." : string.Empty);

    [RelayCommand]
    private void OpenFolder()
    {
        if (session.WorkingDirectory is { Length: > 0 } folder)
        {
            session.OpenPath(folder);
        }
    }

    [RelayCommand]
    private void Rename() => session.BeginRename();

    [RelayCommand]
    private Task StartOverAsync() => startOver?.Invoke() ?? Task.CompletedTask;
}
