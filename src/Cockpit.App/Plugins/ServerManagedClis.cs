using System.Collections.Concurrent;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Plugins.Abstractions.ManagedCli;

namespace Cockpit.App.Plugins;

// AC-1585: the plugin host's managed command-line tools on a server, for a view opened against it (the Claude provider's
// install panel). The synchronous members answer from what the server last said; installing and removing update that.
internal sealed class ServerManagedClis(IPluginChannelRelay relay, string folderId, IReadOnlyDictionary<string, string?> paths, Action<string> report)
{
    private readonly ConcurrentDictionary<string, string?> _paths = new(paths, StringComparer.Ordinal);

    // An older server, or a plugin that is not running there, has none to offer.
    public static async Task<IReadOnlyDictionary<string, string?>> ReadAsync(IPluginChannelRelay relay, string folderId)
    {
        try
        {
            return await relay.GetManagedClisAsync(folderId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new Dictionary<string, string?>();
        }
    }

    public string? Resolve(string cliName) => _paths.GetValueOrDefault(cliName);

    public async Task<ManagedCliStatus> StatusAsync(string cliName, CancellationToken cancellationToken)
    {
        var status = await relay.GetManagedCliStatusAsync(folderId, cliName, cancellationToken);
        return new ManagedCliStatus(status.InstalledVersion, status.LatestVersion);
    }

    public async Task<ManagedCliInstallResult> InstallAsync(string cliName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await relay.InstallManagedCliAsync(folderId, cliName, cancellationToken);
            if (result.Success)
            {
                _paths[cliName] = result.ExecutablePath;
            }

            return new ManagedCliInstallResult(result.Success, result.Version, result.ExecutablePath, result.Error);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ManagedCliInstallResult.Fail(exception.Message);
        }
    }

    // The panel asks synchronously; the server's answer follows, and a failure is reported rather than lost.
    public bool Remove(string cliName)
    {
        if (_paths.GetValueOrDefault(cliName) is null)
        {
            return false;
        }

        _paths[cliName] = null;
        _ = _RemoveAsync(cliName);
        return true;
    }

    public async Task<bool> AutoUpdateAsync(string cliName, CancellationToken cancellationToken) =>
        (await relay.GetManagedCliStatusAsync(folderId, cliName, cancellationToken)).AutoUpdate;

    public Task SetAutoUpdateAsync(string cliName, bool enabled, CancellationToken cancellationToken) =>
        relay.SetManagedCliAutoUpdateAsync(folderId, cliName, enabled, cancellationToken);

    private async Task _RemoveAsync(string cliName)
    {
        try
        {
            await relay.RemoveManagedCliAsync(folderId, cliName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            report(exception.Message);
        }
    }
}
