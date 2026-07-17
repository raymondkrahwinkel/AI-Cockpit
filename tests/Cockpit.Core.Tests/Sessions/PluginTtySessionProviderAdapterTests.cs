using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// <see cref="PluginTtySessionProviderAdapter"/>: the seam between the core's <see cref="ITtySessionProvider"/>
/// vocabulary and a plugin's smaller <see cref="IPluginTtyProvider"/> one (#45 fase B2) — everything in
/// <see cref="TtyLaunchContext"/> reaches the plugin, everything in the plugin's <see cref="PluginTtyLaunchSpec"/>
/// reaches back out as a <see cref="TtyLaunchSpec"/>, and <see cref="SessionResume"/>'s three cases collapse
/// into the plugin's two ("resume this one, or the last one" vs. "nothing" — a plugin has no reason to see
/// the core's own "start fresh" case, since that already reads as no resume at all).
/// </summary>
public class PluginTtySessionProviderAdapterTests
{
    private static (PluginTtySessionProviderAdapter Adapter, IPluginTtyProvider Inner) _CreateAdapter(
        string providerId = "cli-agent-provider.codex", string configJson = """{"Command":"codex"}""")
    {
        var inner = Substitute.For<IPluginTtyProvider>();
        return (new PluginTtySessionProviderAdapter(providerId, inner, configJson, new McpAuthKey()), inner);
    }

    [Fact]
    public void ProviderId_IsWhateverTheAdapterWasConstructedWith()
    {
        var (adapter, _) = _CreateAdapter(providerId: "cli-agent-provider.codex");

        adapter.ProviderId.Should().Be("cli-agent-provider.codex");
    }

    [Fact]
    public void BuildLaunch_PassesTheConfigJsonOptionsWorkingDirectoryAndBaseEnvironmentThroughToThePlugin()
    {
        var (adapter, inner) = _CreateAdapter(configJson: """{"Command":"codex","SandboxMode":"read-only"}""");
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "codex", [], new Dictionary<string, string?>(), "/wd", []));
        var options = new Dictionary<string, string> { ["sandbox"] = "workspace-write" };
        var baseEnvironment = new Dictionary<string, string> { ["PATH"] = "/usr/bin" };
        var context = new TtyLaunchContext(null, options, "/wd", null, baseEnvironment);

        adapter.BuildLaunch(context);

        inner.Received(1).BuildLaunch(Arg.Is<PluginTtyLaunchContext>(pluginContext =>
            pluginContext.ConfigJson == """{"Command":"codex","SandboxMode":"read-only"}"""
            && pluginContext.Options == options
            && pluginContext.WorkingDirectory == "/wd"
            && pluginContext.BaseEnvironment!["PATH"] == "/usr/bin"
            && pluginContext.BaseEnvironment.ContainsKey(WellKnownSessionEnvironment.CockpitMcpKey)
            && pluginContext.Resume == null));
    }

    [Fact]
    public void BuildLaunch_MapsThePluginsSpecFieldsOneToOneOntoTheCoreTtyLaunchSpec()
    {
        var (adapter, inner) = _CreateAdapter();
        var sessionFile = "/tmp/codex-session.json";
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "/usr/local/bin/codex",
            ["--sandbox", "read-only"],
            new Dictionary<string, string?> { ["CODEX_HOME"] = "/home/raymond/.codex-work" },
            "/repo",
            [sessionFile]));
        var context = new TtyLaunchContext(null, new Dictionary<string, string>(), "/repo", null, new Dictionary<string, string>());

        var spec = adapter.BuildLaunch(context);

        spec.ExecutablePath.Should().Be("/usr/local/bin/codex");
        spec.Arguments.Should().Equal("--sandbox", "read-only");
        spec.EnvironmentOverlay.Should().ContainKey("CODEX_HOME").WhoseValue.Should().Be("/home/raymond/.codex-work");
        spec.WorkingDirectory.Should().Be("/repo");
        spec.SessionScopedFiles.Should().Equal(sessionFile);
        spec.StatusFile.Should().BeNull("the plugin TTY contract has no status-file concept — only Claude's own provider reports limits");
    }

    [Fact]
    public void BuildLaunch_ResumeMostRecent_MapsToAPluginTtyResumeWithNoSessionId()
    {
        var (adapter, inner) = _CreateAdapter();
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "codex", [], new Dictionary<string, string?>(), "/wd", []));
        var context = new TtyLaunchContext(null, new Dictionary<string, string>(), "/wd", SessionResume.MostRecent, new Dictionary<string, string>());

        adapter.BuildLaunch(context);

        inner.Received(1).BuildLaunch(Arg.Is<PluginTtyLaunchContext>(pluginContext =>
            pluginContext.Resume == new PluginTtyResume(null)));
    }

    [Fact]
    public void BuildLaunch_ResumeBySessionId_MapsToAPluginTtyResumeCarryingTheTrimmedId()
    {
        var (adapter, inner) = _CreateAdapter();
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "codex", [], new Dictionary<string, string?>(), "/wd", []));
        var context = new TtyLaunchContext(
            null, new Dictionary<string, string>(), "/wd", SessionResume.BySessionId("  thread-123  "), new Dictionary<string, string>());

        adapter.BuildLaunch(context);

        inner.Received(1).BuildLaunch(Arg.Is<PluginTtyLaunchContext>(pluginContext =>
            pluginContext.Resume == new PluginTtyResume("thread-123")));
    }

    [Fact]
    public void BuildLaunch_ResumeBySessionIdWithABlankId_MapsToNoResume_SincePluginsHaveNoInteractivePickerToFallBackOn()
    {
        var (adapter, inner) = _CreateAdapter();
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "codex", [], new Dictionary<string, string?>(), "/wd", []));
        var context = new TtyLaunchContext(
            null, new Dictionary<string, string>(), "/wd", SessionResume.BySessionId("   "), new Dictionary<string, string>());

        adapter.BuildLaunch(context);

        inner.Received(1).BuildLaunch(Arg.Is<PluginTtyLaunchContext>(pluginContext => pluginContext.Resume == null));
    }

    [Fact]
    public void BuildLaunch_StartingFresh_MapsToNoResume()
    {
        var (adapter, inner) = _CreateAdapter();
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "codex", [], new Dictionary<string, string?>(), "/wd", []));
        var context = new TtyLaunchContext(null, new Dictionary<string, string>(), "/wd", SessionResume.New, new Dictionary<string, string>());

        adapter.BuildLaunch(context);

        inner.Received(1).BuildLaunch(Arg.Is<PluginTtyLaunchContext>(pluginContext => pluginContext.Resume == null));
    }
}
