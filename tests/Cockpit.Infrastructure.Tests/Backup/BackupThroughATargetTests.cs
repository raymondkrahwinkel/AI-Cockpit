using System.Text.Json.Nodes;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Backup;
using Cockpit.Core.Profiles;
using Cockpit.Core.Provisioning;
using Cockpit.Infrastructure.Backup;
using Cockpit.Plugins.Abstractions.Backup;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Backup;

/// <summary>
/// AC-1501: a backup handed to a target as a stream, and restored from the stream that target hands back. The target
/// here keeps bytes in memory, the way a plugin's would keep them somewhere that is not this machine's disk.
/// </summary>
public sealed class BackupThroughATargetTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("cockpit-backup-target").FullName;

    private string _Source => Path.Combine(_home, "source");

    // Inside the home rather than being it: the `.replaced-` aside is a sibling of the root.
    private string _Restored => Path.Combine(_home, "restored");

    public void Dispose() => Directory.Delete(_home, recursive: true);

    /// <summary>
    /// Written to the in-memory target, listed there, read back and restored into a fresh root: every file the
    /// archive carries comes back with the content it left with. Nothing touches a path the target owns.
    /// </summary>
    [Fact]
    public async Task ABackupWrittenToATarget_RestoresFromItsStreamWithTheSameContent()
    {
        _Write(_Source, "cockpit.json", """{"Profiles":"mine"}""");
        _Write(_Source, "assistant-memory.md", "what the assistant was told to remember");
        _Write(_Source, "project-logos/acme.png", "not really a png");

        var target = new _InMemoryTarget();
        var service = _Service();

        await service.WriteIntoAsync(
            (archive, token) => target.WriteAsync("backup.zip", archive, token), "memory", _Source, new BackupOptions(), CancellationToken.None);

        var entry = Assert.Single(await target.ListAsync());

        await using (var stream = await target.OpenReadAsync(entry.Id))
        {
            var report = await service.RestoreIntoAsync(stream, _Restored, new RestoreOptions(true, []), null, CancellationToken.None);
            Assert.False(report.Stopped);
        }

        Assert.Equal("mine", JsonNode.Parse(File.ReadAllText(Path.Combine(_Restored, "cockpit.json")))?["Profiles"]?.GetValue<string>());
        Assert.Equal(File.ReadAllText(Path.Combine(_Source, "assistant-memory.md")), File.ReadAllText(Path.Combine(_Restored, "assistant-memory.md")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(_Source, "project-logos", "acme.png")), File.ReadAllBytes(Path.Combine(_Restored, "project-logos", "acme.png")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(BackupService.StagingIn(_Restored)));
    }

    /// <summary>
    /// AC-1593: an organisation that forbids keys and tokens in a backup wins over the checkbox, on every route a
    /// backup is written through, so the archive says it carries none.
    /// </summary>
    [Fact]
    public async Task AForbiddenKeyRing_StaysOutOfTheBackup_EvenWhenAskedFor()
    {
        _Write(_Source, "cockpit.json", """{"Profiles":"mine"}""");
        var org = Substitute.For<IOrgPolicy>();
        org.Current.Returns(new ProvisionPolicy("Acme", Backup: new ProvisionBackupPolicy(IncludeCredentials: "forbidden")));
        var target = new _InMemoryTarget();

        var written = await _Service(org).WriteIntoAsync(
            (archive, token) => target.WriteAsync("backup.zip", archive, token), "memory", _Source, new BackupOptions(IncludeCredentials: true), CancellationToken.None);

        Assert.False(written.IncludesCredentials);
    }

    /// <summary>
    /// AC-1497: a settings save swaps <c>cockpit.json</c> while a backup reads it, and the backup ended in "The process
    /// cannot access the file". Held for a moment, the settings and an archived file are waited out, not lost.
    /// </summary>
    [WindowsFact("Only Windows refuses a read of a file another handle holds without sharing, so elsewhere there is nothing to reproduce.")]
    public async Task ASettingsFileHeldForAMoment_IsWaitedOut_AndTheBackupCarriesIt()
    {
        _Write(_Source, "cockpit.json", """{"Profiles":"mine"}""");
        _Write(_Source, "assistant-memory.md", "what the assistant was told to remember");
        var settings = new FileStream(Path.Combine(_Source, "cockpit.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var memory = new FileStream(Path.Combine(_Source, "assistant-memory.md"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            // Let go one after the other, so each of the two reads has to wait on its own.
            await Task.Delay(300);
            await memory.DisposeAsync();
            await Task.Delay(300);
            await settings.DisposeAsync();
        });

        var target = new _InMemoryTarget();
        var service = _Service();
        await service.WriteIntoAsync(
            (archive, token) => target.WriteAsync("backup.zip", archive, token), "memory", _Source, new BackupOptions(), CancellationToken.None);
        await release;

        await using (var stream = await target.OpenReadAsync(Assert.Single(await target.ListAsync()).Id))
        {
            await service.RestoreIntoAsync(stream, _Restored, new RestoreOptions(true, []), null, CancellationToken.None);
        }

        Assert.Equal("mine", JsonNode.Parse(File.ReadAllText(Path.Combine(_Restored, "cockpit.json")))?["Profiles"]?.GetValue<string>());
        Assert.Equal("what the assistant was told to remember", File.ReadAllText(Path.Combine(_Restored, "assistant-memory.md")));
    }

    /// <summary>
    /// The local folder, the target every cockpit has: a stream that breaks halfway leaves nothing it would list —
    /// not in the folder, and not as a <c>.partial</c> in staging either.
    /// </summary>
    [Fact]
    public async Task AWriteThatBreaksHalfway_LeavesNothingTheLocalFolderLists()
    {
        var folder = Path.Combine(_home, "backups");
        var staging = Path.Combine(_home, "staging");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(staging);
        var target = new LocalFolderBackupTarget(folder, staging);

        await Assert.ThrowsAsync<IOException>(() => target.WriteAsync("backup.zip", new _BreaksAfterOneRead(), CancellationToken.None));

        Assert.Empty(await target.ListAsync());
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    private static void _Write(string root, string relativePath, string content)
    {
        var file = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file) ?? root);
        File.WriteAllText(file, content);
    }

    // No plugins are restored, so neither the stores nor the provisioner is ever asked anything.
    private static BackupService _Service(IOrgPolicy? orgPolicy = null) =>
        new(
            new _NoProfiles(),
            Substitute.For<IPluginStoreConfigStore>(),
            Substitute.For<IPluginStoreClient>(),
            Substitute.For<IPluginProvisioningService>(),
            NullLogger<BackupService>.Instance,
            orgPolicy);

    private sealed class _NoProfiles : ISessionProfileStore
    {
        public Task<IReadOnlyList<SessionProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionProfile>>([]);

        public Task SaveAsync(IReadOnlyList<SessionProfile> profiles, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class _InMemoryTarget : IBackupTarget
    {
        private readonly Dictionary<string, byte[]> _kept = new(StringComparer.Ordinal);

        public string Id => "memory";

        public string DisplayName => "memory";

        public async Task<string> WriteAsync(string name, Stream archive, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await archive.CopyToAsync(copy, cancellationToken);
            _kept[name] = copy.ToArray();

            return name;
        }

        public Task<IReadOnlyList<BackupTargetEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BackupTargetEntry>>(
                _kept.Select(kept => new BackupTargetEntry(kept.Key, DateTimeOffset.UtcNow, kept.Value.Length, null)).ToList());

        public Task<Stream> OpenReadAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(_kept[id], writable: false));
    }

    // Hands over a few bytes, then fails the way a dropped connection does.
    private sealed class _BreaksAfterOneRead : MemoryStream
    {
        private bool _read;

        public _BreaksAfterOneRead()
            : base(new byte[64])
        {
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read)
            {
                throw new IOException("The connection dropped.");
            }

            _read = true;
            buffer.Span[..8].Clear();

            return ValueTask.FromResult(8);
        }
    }
}
