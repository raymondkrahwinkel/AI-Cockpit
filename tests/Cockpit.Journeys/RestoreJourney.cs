using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewTests;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Backup;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Journeys;

// AC-1503: a restore ends in a restart, and the restart says which sign-ins came back empty. A backup made without
// its credentials, restored and restarted, asks for the Depot sign-in; one made with them stays quiet.
[Collection(JourneyCollection.Alone)]
public sealed class RestoreJourney
{
    private const string SeededSettings = """{"Plugins":{"depot":{"Enabled":true,"Data":{"connections":"{\"token\":\"journey-secret\"}"}}}}""";

    [Fact]
    public async Task AfterARestoreAndARestart_TheEmptiedSignInsAreAskedFor_AndNothingIsAskedWhenNoneWere()
    {
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"journey-restore-{Guid.NewGuid():N}")).FullName;
        try
        {
            var stripped = await _BackUpAsync(work, "stripped", includeCredentials: false);
            var whole = await _BackUpAsync(work, "whole", includeCredentials: true);

            var asked = await _RestoreAndRestartAsync(work, "stripped-target", (stripped, ["depot"]));
            Assert.Contains(asked, message => message.Contains("new sign-in", StringComparison.Ordinal) && message.Contains("depot", StringComparison.OrdinalIgnoreCase));

            // A plugin left out of the restore keeps the registration it had, so nothing of its was emptied.
            Assert.Empty(await _RestoreAndRestartAsync(work, "left-out-target", (stripped, [])));

            // The last restore has the last word: one that asks nothing also clears what an earlier one left for the restart.
            Assert.Empty(await _RestoreAndRestartAsync(work, "whole-target", (stripped, ["depot"]), (whole, ["depot"])));
        }
        finally
        {
            JourneyHost.RemoveStateRoot(work);
        }
    }

    private static async Task<string> _BackUpAsync(string work, string name, bool includeCredentials)
    {
        await using var source = JourneyHost.Desktop(root => File.WriteAllText(CockpitConfigPath.For(root), SeededSettings), Directory.CreateDirectory(Path.Combine(work, $"{name}-source")).FullName);
        var archive = Path.Combine(work, $"{name}.zip");
        await source.Services.GetRequiredService<IBackupService>().WriteAsync(archive, new BackupOptions(IncludeCredentials: includeCredentials));

        return archive;
    }

    private static async Task<IReadOnlyList<string>> _RestoreAndRestartAsync(string work, string name, params (string Archive, string[] Plugins)[] restores)
    {
        var target = Directory.CreateDirectory(Path.Combine(work, name)).FullName;

        await using (var before = JourneyHost.Desktop(stateRoot: target))
        {
            var backups = before.Services.GetRequiredService<IBackupService>();

            foreach (var (archive, plugins) in restores)
            {
                await backups.RestoreAsync(archive, new RestoreOptions(Settings: true, plugins));
            }
        }

        await using var after = JourneyHost.Desktop(stateRoot: target);
        await after.StartDesktopAsync();

        return HeadlessAvalonia.Run(() => after.Cockpit.Toasts.Select(toast => toast.Message).ToList());
    }
}
