using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Backup;
using Cockpit.Plugins.Abstractions.Backup;

namespace Cockpit.App.Composition;

// AC-1508: the desktop's view of the targets plugins registered, passed straight through to the backend's registry.
internal sealed class RegisteredBackupTargets(BackupTargets targets) : IRegisteredBackupTargets, ISingletonService
{
    public IReadOnlyList<IBackupTarget> All => targets.All;
}
