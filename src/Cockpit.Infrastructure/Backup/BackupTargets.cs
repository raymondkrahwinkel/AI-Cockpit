using Cockpit.Core.Abstractions;
using Cockpit.Core.Backup;
using Cockpit.Plugins.Abstractions.Backup;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Backup;

// AC-1508: the backup targets plugins registered (ICockpitHost.AddBackupTarget), offered beside the local file in
// Options → Backup and to the scheduled backup. Keyed by id; a taken id, the local folder's included, is ignored.
public sealed class BackupTargets(ILogger<BackupTargets> logger) : ISingletonService
{
    private readonly Lock _gate = new();
    private IReadOnlyList<IBackupTarget> _targets = [];
    private readonly Dictionary<string, string> _plugins = new(StringComparer.Ordinal);

    public IReadOnlyList<IBackupTarget> All => Volatile.Read(ref _targets);

    public IBackupTarget? Find(string id) => All.FirstOrDefault(target => target.Id == id);

    // AC-1663: the plugin that registered a target, or null for the local folder and an unknown id.
    internal string? PluginOf(string targetId)
    {
        lock (_gate)
        {
            return _plugins.GetValueOrDefault(targetId);
        }
    }

    internal bool Add(string pluginId, IBackupTarget target)
    {
        lock (_gate)
        {
            if (target.Id == BackupSchedule.LocalFolderTargetId || Find(target.Id) is not null)
            {
                logger.LogWarning("Backup target '{BackupTarget}' of plugin {PluginId} is ignored; that id is already registered", target.Id, pluginId);
                return false;
            }

            Volatile.Write(ref _targets, [.. _targets, target]);
            _plugins[target.Id] = pluginId;
            return true;
        }
    }

    internal void Remove(string id)
    {
        lock (_gate)
        {
            Volatile.Write(ref _targets, [.. _targets.Where(target => target.Id != id)]);
            _plugins.Remove(id);
        }
    }
}
