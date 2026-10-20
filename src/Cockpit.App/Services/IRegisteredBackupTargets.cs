using Cockpit.Plugins.Abstractions.Backup;

namespace Cockpit.App.Services;

// AC-1508: the backup targets plugins registered, as the Options → Backup screen offers them.
public interface IRegisteredBackupTargets
{
    IReadOnlyList<IBackupTarget> All { get; }
}
