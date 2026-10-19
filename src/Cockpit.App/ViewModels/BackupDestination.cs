using Cockpit.Plugins.Abstractions.Backup;

namespace Cockpit.App.ViewModels;

// AC-1508: where a backup goes — a file the operator picks, the scheduled backup's local folder, or a target a plugin
// registered. Shown by name in a ComboBox.
public sealed record BackupDestination(string Name, string Id, IBackupTarget? Target = null)
{
    public override string ToString() => Name;
}

// One backup a target holds, as the restore list shows it: newest first, by when and how big.
public sealed record BackupTargetChoice(BackupTargetEntry Entry)
{
    public override string ToString() =>
        $"{Entry.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {Math.Max(1, Entry.Size / 1024)} KB"
        + (Entry.AppVersion is { Length: > 0 } version ? $" · {version}" : string.Empty);
}
