using FluentAssertions;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Core.Tests.Configuration;

/// <summary>
/// cockpit.json survives a crash, a kill, and a second writer.
/// <para>
/// It did not: the file was truncated and then streamed into, so for the length of every save the operator's
/// settings existed nowhere. A half-written config does not merely fail to load — the loader treated it as an
/// absent one, started with an empty document, and saved that emptiness back over everything on the next change.
/// Raymond's own config was found damaged on 2026-07-14 (a valid document with the tail of a longer one behind
/// it: two writers), which is what these pin.
/// </para>
/// </summary>
public class ConfigDurabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-durability-{Guid.NewGuid():N}");
    private readonly string _configPath;

    public ConfigDurabilityTests()
    {
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "cockpit.json");
    }

    private McpServerStore Store() => new(_configPath);

    private static McpServerConfig Server(string name) => new()
    {
        Name = name,
        Transport = McpTransport.Http,
        Url = "https://example.invalid",
    };

    [Fact]
    public async Task EverySave_LeavesTheVersionBeforeItAsABackup()
    {
        await Store().SaveAsync([Server("first")]);
        await Store().SaveAsync([Server("second")]);

        File.Exists(_configPath + ".bak").Should().BeTrue();
        (await File.ReadAllTextAsync(_configPath + ".bak")).Should().Contain("first");
    }

    [Fact]
    public async Task ADamagedConfig_IsRecoveredFromItsBackup_NotStartedOverEmpty()
    {
        await Store().SaveAsync([Server("first")]);
        await Store().SaveAsync([Server("second")]);

        // What a kill mid-write leaves behind: a file that is no longer JSON.
        await File.WriteAllTextAsync(_configPath, """{ "McpServers": [ { "Name": "sec""");

        var servers = await Store().LoadAsync();

        servers.Should().ContainSingle().Which.Name.Should().Be("first", "the last version that read cleanly");
        Directory.EnumerateFiles(_directory, "cockpit.json.damaged-*").Should().ContainSingle(
            "the unreadable file is kept, not deleted — it is the operator's, and it may be the only copy of something");
    }

    [Fact]
    public async Task ADamagedConfig_IsNeverSilentlyOverwrittenWithAnEmptyOne()
    {
        // The dangerous path: read fails, caller carries on with a fresh document, and the next save writes that
        // emptiness over every profile, plugin and token. Without a backup to fall back on, refusing is the only
        // honest answer — whatever is in the file is still in the file.
        await File.WriteAllTextAsync(_configPath, "{ this is not json");

        var act = async () => await Store().LoadAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await File.ReadAllTextAsync(_configPath)).Should().Be("{ this is not json", "the file is left exactly as it was");
    }

    [Fact]
    public async Task TheWriteIsAtomic_SoAHalfFileIsNeverWhatTheOperatorIsLeftWith()
    {
        await Store().SaveAsync([Server("first")]);

        // The sibling the write goes to first must not survive it: a leftover .new means the rename never happened.
        File.Exists(_configPath + ".new").Should().BeFalse();
        (await Store().LoadAsync()).Should().ContainSingle();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
