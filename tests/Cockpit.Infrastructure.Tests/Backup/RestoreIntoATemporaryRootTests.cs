using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Backup;
using Cockpit.Infrastructure.Backup;
using Microsoft.Extensions.Logging.Abstractions;
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _Restore(archive, new RestoreOptions(true, []), stage, cancelled.Token));

        // The boundary is what is being asserted: the write stage was never announced, so it was never entered.
        Assert.Equal([RestoreStage.Unpacking], stage.Seen);
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

    private Task _Restore(
        string archive,
        RestoreOptions options,
        IProgress<RestoreStage>? stage = null,
        CancellationToken cancellationToken = default) =>
        new BackupService(Substitute.For<ISessionProfileStore>(), NullLogger<BackupService>.Instance)
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
            new Dictionary<string, string>())));

        return archivePath;
    }

    private static void _Entry(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    // Cancels the moment the restore says it has started unpacking, which is the only way to be standing at the
    // boundary when the token is checked rather than before the restore has done anything at all.
    private sealed class _CancelsOnceUnpacking(CancellationTokenSource source) : IProgress<RestoreStage>
    {
        public List<RestoreStage> Seen { get; } = [];

        public void Report(RestoreStage value)
        {
            Seen.Add(value);
            source.Cancel();
        }
    }
}
