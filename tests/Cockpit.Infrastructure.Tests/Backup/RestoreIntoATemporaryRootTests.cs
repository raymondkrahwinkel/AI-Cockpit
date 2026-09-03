using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Backup;
using Cockpit.Infrastructure.Backup;
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
    /// The cancellation boundary: a restore stopped while it is still unpacking has touched nothing outside its
    /// staging directory, and leaves no staging behind either.
    /// </summary>
    [Fact]
    public async Task ARestoreCancelledWhileUnpacking_LeavesTheCockpitAndItsStagingAsTheyWere()
    {
        _Current("""{"Profiles":"current"}""");

        var archive = _ArchiveWith(("cockpit.json", """{"Profiles":"archived"}"""), ("assistant-memory.md", "archived"));

        using var cancelled = new CancellationTokenSource();
        var stage = new _CancelsOnceUnpacking(cancelled);

        // Was ThrowsAnyAsync<OperationCanceledException> until AC-1281 gave the restore one exit for every stop.
        // What this test guards is untouched — the cockpit and its staging as they were — and is asserted below
        // exactly as it was; only the way the restore says it stopped has changed.
        var report = await _Restore(archive, new RestoreOptions(true, []), stage, cancelled.Token);

        Assert.True(report.Stopped);

        // The boundary is what is being asserted: the write stage was never announced, so it was never entered.
        Assert.Equal([RestoreStage.Unpacking], stage.Seen.Select(seen => seen.Stage));
        Assert.Equal("current", _Restored()["Profiles"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(_Root, "assistant-memory.md")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_Root, BackupContents.StagingFolder)));
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

        await _Restore(archive, new RestoreOptions(false, ["demo"]));

        Assert.Equal("archived", _Restored()["Plugins"]!["demo"]!["Data"]!["board"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(_Root, "plugins", "demo")));
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

    private Task<RestoreReport> _Restore(
        string archive,
        RestoreOptions options,
        IProgress<RestoreProgress>? stage = null,
        CancellationToken cancellationToken = default) =>
        new BackupService(Substitute.For<ISessionProfileStore>(), _log)
            .RestoreIntoAsync(archive, _Root, options, stage, cancellationToken);

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
            new Dictionary<string, string>(),
            _sourceConfigRoot)));

        return archivePath;
    }

    private static void _Entry(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    // AC-695's report is a warning the operator reads, so the test reads the same thing rather than the rewriter's
    // return value — the point is that the restore itself says it, not that a helper could have.
    private sealed class _CapturingLogger : ILogger<BackupService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    // Cancels the moment the restore says it has started unpacking, which is the only way to be standing at the
    // boundary when the token is checked rather than before the restore has done anything at all.
    private sealed class _CancelsOnceUnpacking(CancellationTokenSource source) : IProgress<RestoreProgress>
    {
        public List<RestoreProgress> Seen { get; } = [];

        public void Report(RestoreProgress value)
        {
            Seen.Add(value);
            source.Cancel();
        }
    }
}
