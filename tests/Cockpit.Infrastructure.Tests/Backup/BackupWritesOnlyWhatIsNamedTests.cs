using System.IO.Compression;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Backup;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Backup;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Backup;

/// <summary>
/// AC-1277: what <see cref="BackupService.WriteIntoAsync"/> actually puts in the zip, read back out of the zip rather
/// than asserted against the list it was built from — the list already has its own test, and a list that agrees
/// with itself is what let <c>worktrees</c> and <c>cli</c> into every archive for months.
/// </summary>
public sealed class BackupWritesOnlyWhatIsNamedTests
{
    /// <summary>
    /// One file per path class, laid down in a throwaway state root, and then the archive's own entry names.
    /// Named explicitly here: both assistant files go in (they ride on the general enumeration today, so an
    /// explicit list is exactly where they would fall out silently), and no file out of a plugin folder does.
    /// </summary>
    [Fact]
    public async Task TheArchiveHoldsEveryNamedPath_AndNothingElseTheCockpitDirectoryHappensToContain()
    {
        using var root = new TemporaryRoot();

        string[] expected =
        [
            "cockpit/cockpit.json",
            "cockpit/mcp-permission.json",
            "cockpit/assistant-memory.md",
            "cockpit/assistant-state.md",
            "cockpit/project-logos/acme.png",
        ];

        root.Write("cockpit.json", "{}");
        root.Write("mcp-permission.json", "{}");
        root.Write("assistant-memory.md", "what the assistant was told to remember");
        root.Write("assistant-state.md", "where it left the conversation");
        root.Write("project-logos/acme.png", "not really a png");
        root.Write("plugins/youtrack/plugin.json", """{"id":"youtrack","version":"1.4.0"}""");
        root.Write("plugins/youtrack/Cockpit.Plugins.YouTrack.dll", "megabytes, in real life");
        root.Write("worktree-leases/cockpit.lease", "held by a process that is not running any more");
        root.Write("claude-provider/statusline-relay.ps1", "written by the plugin, rewritten at its next start");
        root.Write("statusline/dead-session.json", "swept by the plugin at its next start");
        root.Write("worktrees/cockpit/branch/Program.cs", "a checkout git can make again");
        root.Write("transcripts/pane.jsonl", "every word anyone said");
        root.Write("logs/cockpit.log", "yesterday's noise");

        var archive = Path.Combine(root.Path, "backup.zip");
        await _Service().WriteIntoAsync(archive, root.Path, new BackupOptions(), CancellationToken.None);

        using var written = ZipFile.OpenRead(archive);
        var entries = written.Entries
            .Select(entry => entry.FullName)
            .Where(name => name != BackupManifest.FileName)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), entries);
    }

    /// <summary>
    /// The assumption AC-1277 splits: leaving a plugin out used to take its <c>cockpit.json</c> registration with
    /// it, because the registration was thought of as belonging to the binaries. It is now the only copy of what
    /// the operator configured, so it stays — while the manifest still leaves the plugin out of what to fetch back.
    /// </summary>
    /// <remarks>
    /// Asserted here too, on the excluded plugin: its <c>PinnedSha256</c> comes through the secret scrub intact. It
    /// is the consent the operator already gave, it travels down the one path in the write that deletes fields, and
    /// a plugin restored without it is a plugin the loader drops to needs-consent. (It is <em>not</em> what tells two
    /// stores publishing the same id apart — it hashes the installed folder, not the zip a store publishes a
    /// <c>Sha256</c> for, so the two never compare equal. See AC-1279.)
    /// </remarks>
    [Fact]
    public async Task APluginLeftOutOfTheArchive_KeepsItsRegistration_AndOnlyLosesItsPlaceInTheManifest()
    {
        using var root = new TemporaryRoot();

        root.Write("cockpit.json", """
            {
              "Plugins": {
                "youtrack": { "Enabled": true, "PinnedSha256": "aaa111", "Data": { "instance": "https://youtrack.example" } },
                "docker": { "Enabled": true, "PinnedSha256": "bbb222", "Data": { "socket": "npipe://./pipe/docker_engine", "token": "s3cr3t" } }
              }
            }
            """);
        root.Write("plugins/youtrack/plugin.json", """{"id":"youtrack","version":"1.4.0"}""");
        root.Write("plugins/docker/plugin.json", """{"id":"docker","version":"2.0.0"}""");

        var archive = Path.Combine(root.Path, "backup.zip");
        var manifest = await _Service().WriteIntoAsync(archive, root.Path, new BackupOptions(Plugins: ["youtrack"]), CancellationToken.None);

        Assert.Equal(["youtrack"], manifest.Plugins.Keys);

        using var written = ZipFile.OpenRead(archive);
        await using var settings = written.GetEntry("cockpit/cockpit.json")!.Open();
        var docker = JsonNode.Parse(settings)!["Plugins"]!["docker"]!;

        Assert.Equal("npipe://./pipe/docker_engine", docker["Data"]!["socket"]!.GetValue<string>());
        Assert.Equal("bbb222", docker["PinnedSha256"]!.GetValue<string>());
        Assert.Equal(string.Empty, docker["Data"]!["token"]!.GetValue<string>());
    }

    /// <summary>
    /// AC-1275: the plugin index names installed plugins, not every folder that happens to sit under
    /// <c>plugins/</c>. A leftover <c>.staging-*</c> extraction carries a real <c>plugin.json</c>, so it read as a
    /// plugin — and the operator's own machine had one, offered back by name in the restore's list.
    /// </summary>
    /// <remarks>
    /// Both of the installer's reserved cases in one test, because they are one line: <c>PluginDiscovery</c>
    /// skipped both and the index skipped neither. The counter-proof is dropping either half of
    /// <c>PluginInstaller.IsInstalledPlugin</c> — the assertion below then names three plugins instead of one.
    /// </remarks>
    [Fact]
    public async Task TheManifestNamesInstalledPluginsOnly_NotTheInstallersOwnFoldersNorOneAlreadyRemoved()
    {
        using var root = new TemporaryRoot();

        root.Write("cockpit.json", "{}");
        root.Write("plugins/youtrack/plugin.json", """{"id":"youtrack","version":"1.4.0"}""");
        root.Write("plugins/.staging-41bb6131d5584f58922b011957565bf0/plugin.json", """{"id":"kubernetes","version":"0.1.2"}""");
        root.Write("plugins/docker/plugin.json", """{"id":"docker","version":"2.0.0"}""");
        root.Write("plugins/docker/.remove", "");

        var archive = Path.Combine(root.Path, "backup.zip");
        var manifest = await _Service().WriteIntoAsync(archive, root.Path, new BackupOptions(), CancellationToken.None);

        Assert.Equal(["youtrack"], manifest.Plugins.Keys);
    }

    // The stores and the provisioner belong to the restore (AC-1279); the write path never reaches for them.
    private static BackupService _Service() =>
        new(
            new NoProfiles(),
            Substitute.For<IPluginStoreConfigStore>(),
            Substitute.For<IPluginStoreClient>(),
            Substitute.For<IPluginProvisioningService>(),
            NullLogger<BackupService>.Instance);

    // The write path only asks for profiles when `IncludeProfileConfigs` is on, which neither test turns on.
    private sealed class NoProfiles : ISessionProfileStore
    {
        public Task<IReadOnlyList<SessionProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionProfile>>([]);

        public Task SaveAsync(IReadOnlyList<SessionProfile> profiles, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    // A cockpit directory of this test's own, handed to `WriteIntoAsync` as its root — the same seam the restore
    // tests use. Not `COCKPIT_STATE_ROOT`: that is process-wide, so it would serialise this whole assembly against
    // everything that resolves a cockpit path, and it can name the operator's real directory. A parameter cannot.
    private sealed class TemporaryRoot : IDisposable
    {
        public TemporaryRoot() => Path = Directory.CreateTempSubdirectory("cockpit-backup-contents").FullName;

        public string Path { get; }

        public void Write(string relativePath, string content)
        {
            var file = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, content);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
