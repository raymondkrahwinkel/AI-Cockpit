using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions.Tty;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

// AC-1705 (L2c): a session started with no briefing still knows how a turn ends and where its mail arrives — after
// the profile's and project's own words, ahead of the statusline line, on the one option every driver reads.
public class SessionBaseInstructionTests
{
    [Fact]
    public async Task BothLaunchRoutes_CarryShortScratchEnvironmentAndInstruction_AndRemoveScratchAfterExit()
    {
        var options = SessionStartComposer.WithInstructions(options: null, systemPrompt: "Be terse.", isJobRun: false);

        Assert.Equal(
            "Be terse.\n\n" + SessionBaseInstruction.Text + "\n\n" + AgentStatusSystemPrompt.Default,
            options[WellKnownPluginSessionOptions.AppendSystemPrompt]);
        Assert.Contains("announced next step", SessionBaseInstruction.Text, StringComparison.Ordinal);
        Assert.Contains("read_inbox", SessionBaseInstruction.Text, StringComparison.Ordinal);

        var pty = Substitute.For<IPtyHostFactory>();
        var process = Substitute.For<IConPtyProcess>();
        IReadOnlyDictionary<string, string> ttyEnvironment = new Dictionary<string, string>();
        pty.Start(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
            Arg.Do<IReadOnlyDictionary<string, string>>(environment => ttyEnvironment = environment), Arg.Any<short>(), Arg.Any<short>()).Returns(process);
        var provider = Substitute.For<ITtySessionProvider>();
        provider.BuildLaunch(Arg.Any<TtyLaunchContext>()).Returns(new TtyLaunchSpec("cli", [], new Dictionary<string, string?>(), ".", []));
        var launcher = new TtyLauncher(pty, Substitute.For<ISessionMemoryLimiter>(), new McpAuthKey(), new SessionMcpKeyring(), NullLogger<TtyLauncher>.Instance);
        var ttyPane = Guid.NewGuid().ToString("N");
        var tty = launcher.Launch(provider, null, options, 80, 24, paneId: ttyPane);
        var ttyPath = ttyEnvironment["COCKPIT_SCRATCH"];
        Assert.InRange(ttyPath.Length, 1, 20);
        Assert.Equal(ttyPath, ttyEnvironment["TEMP"]);
        Assert.Equal(ttyPath, ttyEnvironment["TMP"]);
        Assert.Equal(ttyPath, ttyEnvironment["TMPDIR"]);
        Assert.Equal(ttyPane, SessionScratch.OwnerOf(ttyPath));
        provider.Received().BuildLaunch(Arg.Is<TtyLaunchContext>(context =>
            context.Options[WellKnownPluginSessionOptions.AppendSystemPrompt] == options[WellKnownPluginSessionOptions.AppendSystemPrompt] + "\n\n" + SessionScratch.Instruction(ttyPath)
            && context.BaseEnvironment["COCKPIT_SCRATCH"] == ttyPath));
        process.When(inner => inner.Dispose()).Do(_ => Assert.True(Directory.Exists(ttyPath)));
        tty.Dispose();
        Assert.False(Directory.Exists(ttyPath));

        var plugin = Substitute.For<IPluginSessionDriver>();
        IReadOnlyDictionary<string, string> sdkEnvironment = new Dictionary<string, string>();
        IReadOnlyDictionary<string, string> sdkOptions = new Dictionary<string, string>();
        plugin.StartAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Do<IReadOnlyDictionary<string, string>?>(value => sdkOptions = value ?? new Dictionary<string, string>()), Arg.Any<IReadOnlyList<PluginMcpServer>?>(),
            Arg.Do<IReadOnlyDictionary<string, string>?>(value => sdkEnvironment = value ?? new Dictionary<string, string>()), Arg.Any<IPluginToolset?>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var adapter = new PluginSessionDriverAdapter(plugin, new PluginSessionCapabilities(false, false), new McpAuthKey());
        var sdkPane = Guid.NewGuid().ToString("N");
        await adapter.StartAsync(launchOptions: new Dictionary<string, string>(options) { [WellKnownPluginSessionOptions.PaneId] = sdkPane });
        var sdkPath = sdkEnvironment["COCKPIT_SCRATCH"];
        Assert.InRange(sdkPath.Length, 1, 20);
        Assert.Equal(sdkPath, sdkEnvironment["TEMP"]);
        Assert.Equal(sdkPath, sdkEnvironment["TMP"]);
        Assert.Equal(sdkPath, sdkEnvironment["TMPDIR"]);
        Assert.Equal(sdkPane, SessionScratch.OwnerOf(sdkPath));
        Assert.EndsWith(SessionScratch.Instruction(sdkPath), sdkOptions[WellKnownPluginSessionOptions.AppendSystemPrompt]);
        plugin.DisposeAsync().Returns(_ =>
        {
            Assert.True(Directory.Exists(sdkPath));
            return ValueTask.CompletedTask;
        });
        await adapter.DisposeAsync();
        Assert.False(Directory.Exists(sdkPath));
    }

    // AC-1942 (L2 data loss): a scratch directory without a marker is set aside intact, not a permanent block.
    [Fact]
    public void Create_DirectoryWithoutMarker_MovesItAsideIntactAndStartsAFreshOwnedOne()
    {
        var previous = Environment.GetEnvironmentVariable(SessionScratch.BaseVariable);
        var basis = Path.Combine(Path.GetTempPath(), $"cockpit-scratch-{Guid.NewGuid():N}");
        try
        {
            Environment.SetEnvironmentVariable(SessionScratch.BaseVariable, basis);
            var pane = Guid.NewGuid().ToString("N");
            var path = SessionScratch.PathFor(pane);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "render.png"), "keep me");

            var created = SessionScratch.Create(pane);

            Assert.Equal(path, created);
            Assert.Equal(pane, SessionScratch.OwnerOf(created));
            var orphan = Assert.Single(Directory.GetDirectories(basis, Path.GetFileName(path) + "-orphan-*"));
            Assert.Equal("keep me", File.ReadAllText(Path.Combine(orphan, "render.png")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SessionScratch.BaseVariable, previous);
            Directory.Delete(basis, recursive: true);
        }
    }
}
