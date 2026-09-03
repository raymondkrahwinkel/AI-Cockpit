using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Backup;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Backup;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Backup;

/// <summary>
/// AC-1278: the restore reading a schema-2 archive, driven end to end — unpack, merge, set aside — against a
/// temporary root. Never the operator's own cockpit directory: this is the one operation here that, pointed at
/// the wrong root, costs them everything they set up.
/// </summary>
public sealed class RestoreIntoATemporaryRootTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("cockpit-restore-test").FullName;

    private readonly _CapturingLogger _log = new();

    // The config root the archive says it was made on (AC-695). Set per test rather than passed to `_ArchiveWith`,
    // which takes the entries as `params`.
    private string? _sourceConfigRoot;

    // The store the restore fetches from, stood up in the test rather than reached over the network: what it
    // publishes, what the archive says each plugin was at, which downloads break, and what actually got installed.
    private readonly List<PluginStoreEntry> _published = [];

    private readonly Dictionary<string, string> _archivedVersions = new(StringComparer.Ordinal);

    private readonly HashSet<string> _unreachableZips = new(StringComparer.Ordinal);

    private readonly List<string> _installed = [];

    // What cockpit.json held at the moment the first install ran — the proof that a plugin's settings are written
    // after it is back, not before.
    private string? _settingsWhenInstalling;

    // Cancelled once one plugin has been installed whole, which is the only way to be standing between two of them
    // when the token is read rather than before the fetch has done anything.
    private CancellationTokenSource? _stopAfterInstalling;

    // The same trick one step earlier: cancelled when the first store index is asked for, which is where a stop
    // used to leave as an exception instead of a report.
    private CancellationTokenSource? _stopWhenReadingTheStores;

    // The stores this cockpit has configured, and the ones whose index cannot be read — a local store on a drive
    // that is not this machine's is the case a restore has to tell apart from "nobody publishes it any more".
    private List<PluginStoreConfig> _configuredStores = [PluginStoreConfig.Remote("https://store.test")];

    private readonly HashSet<string> _unreadableStores = new(StringComparer.Ordinal);

    // The cockpit directory sits inside the temp home rather than being it: the `.replaced-` directory is a sibling
    // of the root, so a root without a parent to write it into would not be the arrangement being tested.
    private string _Root => Path.Combine(_home, "cockpit");

    public void Dispose() => Directory.Delete(_home, recursive: true);

    /// <summary>
    /// The merge, in both directions the operator can ask for: settings on takes the archive's own keys, settings
    /// off keeps this cockpit's — and either way only the chosen plugins move, key by key.
    /// </summary>
    [Theory]
    [InlineData(true, "archived")]
    [InlineData(false, "current")]
    public async Task RestoringCockpitJson_MergesItKeyByKey(bool settings, string expectedProfiles)
    {
        _Current("""{"Profiles":"current","Plugins":{"kept":{"Data":{"k":"current"}},"chosen":{"Data":{"k":"current"}}}}""");

        var archive = _ArchiveWith(
            ("cockpit.json", """{"Profiles":"archived","Plugins":{"chosen":{"Data":{"k":"archived"}},"unchosen":{"Data":{"k":"archived"}}}}"""));

        await _Restore(archive, new RestoreOptions(settings, ["chosen"]));

        var result = _Restored();
        Assert.Equal(expectedProfiles, result["Profiles"]!.GetValue<string>());
        Assert.Equal("current", result["Plugins"]!["kept"]!["Data"]!["k"]!.GetValue<string>());
        Assert.Equal("archived", result["Plugins"]!["chosen"]!["Data"]!["k"]!.GetValue<string>());
        Assert.Null(result["Plugins"]!["unchosen"]);
    }

    /// <summary>
    /// The cancellation boundary, at both moments a stop can land before the settings are written: while unpacking,
    /// and while the stores are being read for the plugins to fetch back. Both come back as a report and not as an
    /// exception — the second one threw until the epic's end gate, and the wizard reads an exception as "the restore
    /// failed", which a stop is not. Either way the cockpit and its staging are as they were.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARestoreStoppedBeforeTheSettingsAreWritten_ComesBackAsAReport(bool whileTheStoresAreRead)
    {
        _Current("""{"Profiles":"current"}""");
        _archivedVersions["demo"] = "1.0.0";
        _Publish("demo", _Version("demo", "1.0.0"));

        var archive = _ArchiveWith(("cockpit.json", """{"Profiles":"archived"}"""), ("assistant-memory.md", "archived"));

        using var cancelled = new CancellationTokenSource();

        if (whileTheStoresAreRead)
        {
            _stopWhenReadingTheStores = cancelled;
        }

        // Was ThrowsAnyAsync<OperationCanceledException> until AC-1281 gave the restore one exit for every stop.
        // What this test guards is untouched — the cockpit and its staging as they were — and is asserted below
        // exactly as it was; only the way the restore says it stopped has changed.
        var stage = new _Stages(whileTheStoresAreRead ? null : cancelled);
        var report = await _Restore(archive, new RestoreOptions(true, ["demo"]), stage, cancelled.Token);

        Assert.True(report.Stopped);

        // The boundary is what is being asserted: neither the fetch nor the write stage was ever announced, so
        // neither was entered, and nothing was installed on the way past.
        Assert.Equal([RestoreStage.Unpacking], stage.Seen.Select(seen => seen.Stage));
        Assert.Equal("current", _Restored()["Profiles"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(_Root, "assistant-memory.md")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_Root, BackupContents.StagingFolder)));
        Assert.Empty(_installed);

        // A stop that got as far as the stores names the plugin it never reached; one stopped before that has
        // nothing to name yet. Either way the operator is not left guessing which of the two happened.
        Assert.Equal(whileTheStoresAreRead ? ["demo"] : [], report.Notes.Select(note => note.Id));
    }

    /// <summary>
    /// The safety valve, contents and all — including a file inside a folder, which is what the assistant's memory
    /// and the project logos both depend on and what a non-recursive walk restored as nothing.
    /// </summary>
    [Fact]
    public async Task WhatARestoreReplaces_IsKeptWithItsContentsInTheReplacedDirectory()
    {
        _Current("""{"Profiles":"current"}""");
        _Write(Path.Combine(_Root, "assistant-memory.md"), "before");
        _Write(Path.Combine(_Root, "project-logos", "acme.svg"), "before");

        var archive = _ArchiveWith(
            ("cockpit.json", """{"Profiles":"archived"}"""),
            ("assistant-memory.md", "after"),
            ("project-logos/acme.svg", "after"));

        await _Restore(archive, new RestoreOptions(true, []));

        var aside = Assert.Single(Directory.EnumerateDirectories(_home, "cockpit.replaced-*"));
        Assert.Equal("""{"Profiles":"current"}""", File.ReadAllText(Path.Combine(aside, "cockpit.json")));
        Assert.Equal("before", File.ReadAllText(Path.Combine(aside, "assistant-memory.md")));
        Assert.Equal("before", File.ReadAllText(Path.Combine(aside, "project-logos", "acme.svg")));

        Assert.Equal("after", File.ReadAllText(Path.Combine(_Root, "assistant-memory.md")));
        Assert.Equal("after", File.ReadAllText(Path.Combine(_Root, "project-logos", "acme.svg")));
    }

    /// <summary>
    /// A schema-2 archive carries no plugin binaries, so every restore is this case: the registration is what holds
    /// what the plugin stored, and it must come back whether or not the plugin itself has been fetched again.
    /// </summary>
    [Fact]
    public async Task APluginWhoseBinariesAreNotBackYet_StillGetsItsSettings()
    {
        _Current("""{"Plugins":{}}""");

        var archive = _ArchiveWith(("cockpit.json", """{"Plugins":{"demo":{"Enabled":true,"Data":{"board":"archived"}}}}"""));

        var report = await _Restore(archive, new RestoreOptions(false, ["demo"]));

        Assert.Equal("archived", _Restored()["Plugins"]!["demo"]!["Data"]!["board"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(_Root, "plugins", "demo")));

        // Re-read rather than only kept green (AC-1281 asked for exactly this): the assertion above used to mean
        // "nothing is fetched at all", and since AC-1279 it means "the fetch ran and could not get it". The stronger
        // claim is what the operator is now told — named, with the reason, instead of absent from the report.
        var stillMissing = Assert.Single(report.Notes);
        Assert.Equal("demo", stillMissing.Id);
        Assert.Contains("none of the stores carries it", stillMissing.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// AC-695: an archive made on one platform, restored on the other. Whatever the backup machine kept under its own
    /// config root is re-anchored on this machine's root, with this machine's separators; whatever lies outside it and
    /// does exist here is left exactly as it stands.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\raymond\AppData\Roaming\Cockpit", '\\')]
    [InlineData("/home/raymond/.config/Cockpit", '/')]
    public async Task PathsUnderTheBackupMachinesConfigRoot_AreReAnchoredOnThisOne(string sourceConfigRoot, char separator)
    {
        var ownFolder = Directory.CreateDirectory(Path.Combine(_home, "still-here")).FullName;
        var logo = $"{sourceConfigRoot}{separator}project-logos{separator}acme.svg";
        var worktree = $"{sourceConfigRoot}{separator}worktrees{separator}acme{separator}feature";

        _sourceConfigRoot = sourceConfigRoot;
        _Current("""{"Projects":[]}""");

        var archive = _ArchiveWith(("cockpit.json", JsonSerializer.Serialize(new
        {
            Projects = new[] { new { Name = "Acme", SourceDirectory = ownFolder, LogoPath = logo } },
            Worktrees = new[] { new { Path = worktree } },
        })));

        await _Restore(archive, new RestoreOptions(true, []));

        var project = _Restored()["Projects"]![0]!;
        Assert.Equal(Path.Combine(_Root, "project-logos", "acme.svg"), project["LogoPath"]!.GetValue<string>());
        Assert.Equal(Path.Combine(_Root, "worktrees", "acme", "feature"), _Restored()["Worktrees"]![0]!["Path"]!.GetValue<string>());
        Assert.Equal(ownFolder, project["SourceDirectory"]!.GetValue<string>());
        Assert.Empty(_log.Warnings);

        // Pinned here because it is the whole reason the rewrite is plain text and not System.IO.Path: Path leaves a
        // path alone only in its own platform's shape and silently re-roots any other, which on the row that is
        // foreign to this machine would turn a stored folder into one under the working directory.
        Assert.Equal(Path.GetFullPath(sourceConfigRoot) == sourceConfigRoot, Path.GetFullPath(logo) == logo);
    }

    /// <summary>
    /// AC-695: a project folder outside the config root that this machine does not have. Neither dropped nor pointed
    /// somewhere plausible — it stays in the settings, visibly wrong, and the restore says which project it belongs to.
    /// </summary>
    [Fact]
    public async Task AProjectFolderThatDoesNotExistHere_IsReportedRatherThanRewrittenOrDropped()
    {
        // The other platform's shape on purpose — the ticket's own case, and the one folder that is certainly absent.
        var gone = OperatingSystem.IsWindows() ? "/home/raymond/Projects/Cockpit" : @"D:\Projects\dotnet\Cockpit";

        _sourceConfigRoot = @"C:\Users\raymond\AppData\Roaming\Cockpit";
        _Current("""{"Projects":[]}""");

        var archive = _ArchiveWith(("cockpit.json", JsonSerializer.Serialize(new
        {
            Projects = new[] { new { Name = "Cockpit", SourceDirectory = gone } },
        })));

        await _Restore(archive, new RestoreOptions(true, []));

        Assert.Equal(gone, _Restored()["Projects"]![0]!["SourceDirectory"]!.GetValue<string>());

        var warning = Assert.Single(_log.Warnings);
        Assert.Contains("Cockpit", warning);
        Assert.Contains(gone, warning);
    }

    /// <summary>
    /// AC-1279, every way a plugin can come out of a restore, and what the operator is told about each. Only the
    /// first row is silent: getting back exactly what you backed up is the one outcome with nothing to say. The rest
    /// each owe a note — including the two that used to go only to the log, which for the operator is silence: put
    /// back on a version other than the archive's, and left alone because a newer one is installed here.
    /// </summary>
    [Theory]
    [InlineData("still published", "1.0.0", null)]
    [InlineData("gone from the store", null, "none of the stores carries it any more")]
    [InlineData("published but incompatible", null, "this cockpit cannot run it")]
    [InlineData("only a newer version left", "2.0.0", "put back on 2.0.0 instead")]
    [InlineData("its store is a path this machine does not have", null, @"the local store 'D:\plugin-store' could not be read")]
    [InlineData("a newer one is already installed here", null, "newer than the 1.0.0 in the backup")]
    public async Task ARestoredPlugin_ComesBackFromItsStore_OrSaysWhyNotAndWhatChanged(string store, string? expected, string? noted)
    {
        _Current("""{"Plugins":{}}""");
        _archivedVersions["demo"] = "1.0.0";

        switch (store)
        {
            case "still published":
                _Publish("demo", _Version("demo", "1.0.0"));
                break;
            case "published but incompatible":
                _Publish("demo", _Version("demo", "1.0.0", abstractions: AbstractionsContract.Version + 97));
                break;
            case "only a newer version left":
                _Publish("demo", _Version("demo", "2.0.0"));
                break;
            case "its store is a path this machine does not have":
                _configuredStores = [PluginStoreConfig.Local(@"D:\plugin-store")];
                _unreadableStores.Add(@"D:\plugin-store");
                break;
            case "a newer one is already installed here":
                // On disk past what the backup holds. `PluginSourceInstaller` never rolls that back and a restore
                // must not either — so nothing is fetched, and the operator is told why it stayed as it is.
                _Publish("demo", _Version("demo", "1.0.0"));
                _Write(Path.Combine(_Root, "plugins", "demo", "plugin.json"), """{"id":"demo","version":"1.2.0"}""");
                break;
        }

        var archive = _ArchiveWith(("cockpit.json", """{"Plugins":{"demo":{"Enabled":true,"PinnedSha256":"archived-pin"}}}"""));

        var report = await _Restore(archive, new RestoreOptions(false, ["demo"]));

        string[] installed = expected is null ? [] : [$"demo-{expected}"];
        Assert.Equal(installed, _installed);
        Assert.Equal(
            expected is null ? "archived-pin" : $"sha-of-{expected}",
            _Restored()["Plugins"]!["demo"]!["PinnedSha256"]!.GetValue<string>());

        // Asserted on the report and not on the log: the report is what reaches the operator, and a log line is the
        // thing this ticket kept mistaking for telling them.
        if (noted is null)
        {
            Assert.Empty(report.Notes);
        }
        else
        {
            Assert.Contains(noted, Assert.Single(report.Notes).Note, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The order the whole of AC-1279 turns on: the binaries first, their settings after — and a plugin that never
    /// arrived keeps everything it stored, so it works the moment its store is reachable again.
    /// </summary>
    [Fact]
    public async Task PluginSettings_AreWrittenAfterTheInstall_AndOutliveAPluginThatNeverArrived()
    {
        _Current("""{"Plugins":{}}""");
        _archivedVersions["landed"] = _archivedVersions["gone"] = _archivedVersions["unsupported"] = "1.0.0";
        _Publish("landed", _Version("landed", "1.0.0"));
        _Publish("unsupported", _Version("unsupported", "1.0.0", abstractions: AbstractionsContract.Version + 97));

        var archive = _ArchiveWith(("cockpit.json", """
            {"Plugins":{
              "landed":{"Enabled":true,"PinnedSha256":"archived-pin","MenuOrder":4,"Data":{"board":"archived"}},
              "gone":{"Enabled":true,"PinnedSha256":"archived-pin","HiddenInMenu":true,"Data":{"board":"archived"}},
              "unsupported":{"Enabled":true,"PinnedSha256":"archived-pin","Data":{"board":"archived"}}}}
            """));

        await _Restore(archive, new RestoreOptions(false, ["landed", "gone", "unsupported"]));

        // The install ran while cockpit.json still said what it said before the restore: the settings that belong to
        // a plugin are written once it is back, never ahead of it.
        Assert.Equal("""{"Plugins":{}}""", _settingsWhenInstalling);

        var restored = _Restored()["Plugins"]!;
        Assert.Equal("sha-of-1.0.0", restored["landed"]!["PinnedSha256"]!.GetValue<string>());
        Assert.Equal(4, restored["landed"]!["MenuOrder"]!.GetValue<int>());
        Assert.Equal("archived", restored["landed"]!["Data"]!["board"]!.GetValue<string>());

        foreach (var withoutBinaries in new[] { "gone", "unsupported" })
        {
            Assert.Equal("archived-pin", restored[withoutBinaries]!["PinnedSha256"]!.GetValue<string>());
            Assert.Equal("archived", restored[withoutBinaries]!["Data"]!["board"]!.GetValue<string>());
        }

        Assert.True(restored["gone"]!["HiddenInMenu"]!.GetValue<bool>());
    }

    /// <summary>
    /// One plugin the store cannot hand over is that plugin's problem: the ones on either side of it in the batch
    /// still land, which is what makes a restore of eleven plugins worth starting at all.
    /// </summary>
    [Fact]
    public async Task OnePluginTheStoreCannotHandOver_DoesNotHoldUpTheRest()
    {
        _Current("""{"Plugins":{}}""");

        foreach (var id in new[] { "first", "broken", "last" })
        {
            _archivedVersions[id] = "1.0.0";
            _Publish(id, _Version(id, "1.0.0"));
        }

        _unreachableZips.Add("broken-1.0.0.zip");

        var archive = _ArchiveWith(("cockpit.json", """{"Plugins":{"first":{},"broken":{},"last":{}}}"""));

        await _Restore(archive, new RestoreOptions(false, ["first", "broken", "last"]));

        Assert.Equal(["first-1.0.0", "last-1.0.0"], _installed);
    }

    /// <summary>
    /// Stopping a fetch that runs for minutes (Raymond, AC-1279): honoured between plugins and never inside one, so
    /// what landed is whole plugins. Nothing is rolled back, and the settings are never written — a restore stopped
    /// here cost the restore, not the cockpit.
    /// </summary>
    /// <remarks>
    /// A method rather than a row of the theory above: this one needs a token and ends in a thrown cancellation, not
    /// in a restored plugin. What it guards is the state, not the shape — if the way a stop reports itself changes
    /// (AC-1281 turns it into a returned report), these same two assertions must survive the change.
    /// </remarks>
    [Fact]
    public async Task AFetchStoppedBetweenPlugins_KeepsWhatLanded_AndLeavesTheSettingsAlone()
    {
        _Current("""{"Plugins":{}}""");

        foreach (var id in new[] { "first", "second", "third" })
        {
            _archivedVersions[id] = "1.0.0";
            _Publish(id, _Version(id, "1.0.0"));
        }

        using var cancelled = new CancellationTokenSource();
        _stopAfterInstalling = cancelled;

        var archive = _ArchiveWith(("cockpit.json", """{"Plugins":{"first":{"Data":{"board":"archived"}}}}"""));

        // Was ThrowsAnyAsync<OperationCanceledException> until AC-1281 gave the restore one exit for every stop.
        // What this test guards is untouched and asserted below exactly as it was; only the way the restore says
        // it stopped has changed — thrown now means it never really began, returned means it ran and was stopped.
        var report = await _Restore(archive, new RestoreOptions(false, ["first", "second", "third"]), cancellationToken: cancelled.Token);

        Assert.True(report.Stopped);

        // The first plugin was installed whole and stays; the second was never begun.
        Assert.Equal(["first-1.0.0"], _installed);
        Assert.Equal("""{"Plugins":{}}""", File.ReadAllText(Path.Combine(_Root, "cockpit.json")));

        // A stop leaves no mystery: everything it did not reach is named, and the one that landed is not in the list.
        Assert.Equal(["second", "third"], report.Notes.Select(plugin => plugin.Id));
        Assert.All(report.Notes, plugin => Assert.Contains("stopped before it was fetched", plugin.Note, StringComparison.Ordinal));
    }

    private Task<RestoreReport> _Restore(
        string archive,
        RestoreOptions options,
        IProgress<RestoreProgress>? stage = null,
        CancellationToken cancellationToken = default)
    {
        var storeClient = Substitute.For<IPluginStoreClient>();
        storeClient.FetchIndexAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _stopWhenReadingTheStores?.Cancel();

                // A real client aborts a request that is on the wire rather than finishing it, so the stub does too:
                // otherwise the one case the restore has to survive would never reach it.
                call.ArgAt<CancellationToken>(1).ThrowIfCancellationRequested();

                return _unreadableStores.Contains(call.ArgAt<PluginStoreConfig>(0).Location)
                    ? new PluginStoreFetchResult(false, "The directory does not exist.", null, null)
                    : new PluginStoreFetchResult(true, null, new PluginStoreIndex("test", _published), "https://store.test/index.json");
            });
        storeClient.DownloadZipAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => _unreachableZips.Contains(call.ArgAt<string>(1))
                ? new PluginStoreDownloadResult(false, "The store dropped the connection.", null)
                : new PluginStoreDownloadResult(true, null, Path.Combine(_home, call.ArgAt<string>(1))));

        var stores = Substitute.For<IPluginStoreConfigStore>();
        stores.LoadAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<PluginStoreConfig>>(_configuredStores);

        return new BackupService(
                Substitute.For<ISessionProfileStore>(),
                stores,
                storeClient,
                new PluginProvisioningService(storeClient, new _RecordingInstaller(this)),
                _log)
            .RestoreIntoAsync(archive, _Root, options, stage, cancellationToken);
    }

    // The one place a version's zip name is composed, so what the store publishes and what the installer reports
    // having installed cannot drift apart.
    private static PluginStoreVersion _Version(string id, string version, int abstractions = AbstractionsContract.Version) =>
        new(version, $"{id}-{version}.zip", abstractions, MinHostVersion: null, Sha256: null, Notes: null);

    private void _Publish(string id, params PluginStoreVersion[] versions) =>
        _published.Add(new PluginStoreEntry(id, id, null, null, versions[^1].Version, versions));

    // Stands in for the real installer, which would need a genuine plugin zip: it reports the id and checksum an
    // install would have landed on, and notes what cockpit.json held while it ran.
    private sealed class _RecordingInstaller(RestoreIntoATemporaryRootTests test) : IPluginInstaller
    {
        public Task<PluginInstallResult> InstallFromZipAsync(
            string zipFilePath, int hostAbstractionsMajor, Version? hostVersion = null, CancellationToken cancellationToken = default)
        {
            var name = Path.GetFileNameWithoutExtension(zipFilePath);
            test._installed.Add(name);

            var settings = Path.Combine(test._Root, "cockpit.json");
            test._settingsWhenInstalling ??= File.Exists(settings) ? File.ReadAllText(settings) : "";

            test._stopAfterInstalling?.Cancel();

            var split = name.LastIndexOf('-');

            return Task.FromResult(PluginInstallResult.Success(name[..split], $"sha-of-{name[(split + 1)..]}"));
        }

        public Task MarkForRemovalAsync(string folderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SweepRemovalsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SweepPendingUpdatesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private void _Current(string settings) => _Write(Path.Combine(_Root, "cockpit.json"), settings);

    private JsonObject _Restored() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(_Root, "cockpit.json")))!;

    private static void _Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // Names are spelled out here on purpose: this is where the archive is composed, so hard-coding is stating the
    // input rather than assuming what `BackupContents.Included` happens to hold today.
    private string _ArchiveWith(params (string Path, string Content)[] files)
    {
        var archivePath = Path.Combine(_home, $"{Guid.NewGuid():n}.zip");

        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);

        foreach (var (path, content) in files)
        {
            _Entry(archive, $"cockpit/{path}", content);
        }

        _Entry(archive, BackupManifest.FileName, JsonSerializer.Serialize(new BackupManifest(
            BackupManifest.CurrentSchema,
            "test",
            DateTimeOffset.UtcNow,
            IncludesCredentials: false,
            [],
            new Dictionary<string, string>(),
            _archivedVersions,
            _sourceConfigRoot)));

        return archivePath;
    }

    private static void _Entry(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    // AC-695's report is a warning the operator reads, so the test reads the same thing rather than the rewriter's
    // return value — the point is that the restore itself says it, not that a helper could have. AC-1279 needs the
    // rest too: what it says per plugin is an information line, and `Warnings` alone would not see it.
    private sealed class _CapturingLogger : ILogger<BackupService>
    {
        public List<string> Warnings { get; } = [];

        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            Lines.Add(line);

            if (level == LogLevel.Warning)
            {
                Warnings.Add(line);
            }
        }
    }

    // Records the stages, and — when it is handed a source — cancels at the first one, which is the only way to be
    // standing at the boundary when the token is checked rather than before the restore has done anything at all.
    private sealed class _Stages(CancellationTokenSource? cancelAtTheFirst) : IProgress<RestoreProgress>
    {
        public List<RestoreProgress> Seen { get; } = [];

        public void Report(RestoreProgress value)
        {
            Seen.Add(value);
            cancelAtTheFirst?.Cancel();
        }
    }
}
