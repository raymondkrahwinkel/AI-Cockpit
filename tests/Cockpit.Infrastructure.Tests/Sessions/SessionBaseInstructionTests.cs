using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Projects;
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
        await _AssertTheBrainLeadsAndIsResolvedOnce(options);

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

    // AC-1992: the brain leads the system prompt and also rides alone under its own key. The project's brain beats the
    // profile's, never both; none means no key; a brain on a server no source owns is said to be unavailable; a
    // composed start with its own system prompt still gets it; a legacy name stays the assistant sentence instead.
    private static async Task _AssertTheBrainLeadsAndIsResolvedOnce(IReadOnlyDictionary<string, string> withoutBrain)
    {
        var withBrain = SessionStartComposer.WithInstructions(options: null, systemPrompt: "Be terse.", isJobRun: false, brainInstruction: "Use brain X.");
        Assert.Equal(
            "Use brain X.\n\nBe terse.\n\n" + SessionBaseInstruction.Text + "\n\n" + AgentStatusSystemPrompt.Default,
            withBrain[WellKnownPluginSessionOptions.AppendSystemPrompt]);
        Assert.Equal("Use brain X.", withBrain[WellKnownPluginSessionOptions.BrainInstruction]);
        Assert.False(withoutBrain.ContainsKey(WellKnownPluginSessionOptions.BrainInstruction));

        var project = Project.Create("Cockpit") with { Assistant = "depot:acme/aura@depot.acme.example" };
        var projectStore = Substitute.For<IProjectStore>();
        projectStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ProjectSettings { Projects = [project] });
        var sources = Substitute.For<IProjectMemorySourceRegistry>();
        sources.Sources.Returns([new ProjectMemorySourceRegistration("depot", "Depot project", "Read it.")
        {
            BrainInstruction = value => value.EndsWith("@depot.acme.example", StringComparison.Ordinal) ? $"Bootstrap {value}." : null,
        }]);
        var composer = new SessionStartComposer(projectStore, Substitute.For<IMcpServerCatalog>(), memorySources: sources);
        var profile = new SessionProfile("work", new ClaudeConfig("/c")) { Assistant = "depot:acme/zyra@depot.acme.example" };
        SessionLaunchRequest Composed(string? projectId, SessionProfile under) =>
            new("desk", under, null, null, null, null, null, null, projectId, false) { IsComposed = true, SystemPrompt = "Explicit start text." };

        var both = await composer.BrainInstructionAsync(Composed(project.Id, profile));
        Assert.Equal(SessionStartDefaults.BrainNote("acme/aura@depot.acme.example", "Bootstrap acme/aura@depot.acme.example."), both);
        Assert.StartsWith("Use brain acme/aura@depot.acme.example; " + SessionStartDefaults.ClaudeMdBrainChoiceLapses, both);
        Assert.Contains("do not ask which assistant, persona or brain to load", both);
        Assert.EndsWith(SessionStartDefaults.ExplicitBrainWins, both);
        Assert.DoesNotContain("zyra", both);
        Assert.Equal(
            SessionStartDefaults.BrainNote("acme/zyra@depot.acme.example", "Bootstrap acme/zyra@depot.acme.example."),
            await composer.BrainInstructionAsync(Composed(null, profile)));
        Assert.Null(await composer.BrainInstructionAsync(Composed(null, profile with { Assistant = null })));
        Assert.Equal(
            SessionStartDefaults.UnavailableBrainNote("acme/zyra@depot.other.example"),
            await composer.BrainInstructionAsync(Composed(null, profile with { Assistant = "depot:acme/zyra@depot.other.example" })));

        var memory = sources.Sources.ToMemorySources();
        Assert.DoesNotContain("This session runs as", SessionStartDefaults.Resolve(project, profile, memorySources: memory).SystemPrompt ?? string.Empty);
        Assert.Null(SessionStartDefaults.ResolveBrainInstruction(null, profile with { Assistant = "Zyra" }, memory));
        Assert.StartsWith("This session runs as Zyra.", SessionStartDefaults.Resolve(null, profile with { Assistant = "Zyra" }, memorySources: memory).SystemPrompt);
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
