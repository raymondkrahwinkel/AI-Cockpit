using Microsoft.Extensions.Options;
using NSubstitute;
using FluentAssertions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;

namespace Cockpit.Core.Tests.Claude;

/// <summary>
/// Covers <see cref="ClaudeTtySessionProvider"/>'s composition seam: it must resolve the executable
/// path, mark the working directory trusted, build the environment overlay and launch-only start-default
/// arguments (permission mode/model/effort), and hand all of that back as a <see cref="TtyLaunchSpec"/> —
/// without spawning anything itself. Spawning is <c>TtyLauncher</c>'s job, covered separately in
/// <c>Sessions.TtyLauncherTests</c> against a substituted <see cref="ITtySessionProvider"/>.
/// </summary>
public class ClaudeTtySessionProviderBuildLaunchTests : IDisposable
{
    private readonly string _configDir = Directory.CreateTempSubdirectory("cockpit-tty-provider-tests-").FullName;
    private readonly IMcpServerStore _emptyMcpStore = CreateEmptyMcpStore();

    // An empty shared registry, so _WriteRegistryMcpConfig produces no --mcp-config and the argument
    // assertions below stay about the start-default flags only.
    private static IMcpServerStore CreateEmptyMcpStore()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync().Returns(Task.FromResult<IReadOnlyList<McpServerConfig>>([]));
        return store;
    }

    private static TtyLaunchContext Context(
        SessionProfile? profile,
        IReadOnlyDictionary<string, string>? options = null,
        string? workingDirectory = null,
        SessionResume? resume = null) =>
        new(
            profile,
            options ?? new Dictionary<string, string>(),
            workingDirectory ?? Directory.GetCurrentDirectory(),
            resume,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private ClaudeTtySessionProvider _CreateProvider(
        CockpitOptions? options = null,
        IClaudeExecutableLocator? executableLocator = null) =>
        new(
            Options.Create(options ?? new CockpitOptions()),
            executableLocator ?? Substitute.For<IClaudeExecutableLocator>(),
            new WorkspaceTrustWriter(),
            _emptyMcpStore);

    [Fact]
    public void BuildLaunch_WithAProfile_SetsClaudeConfigDirInTheEnvironmentOverlay()
    {
        var provider = _CreateProvider();
        var profile = new SessionProfile("Personal", new ClaudeConfig(_configDir, "/usr/bin/claude"));

        var spec = provider.BuildLaunch(Context(profile));

        spec.EnvironmentOverlay["CLAUDE_CONFIG_DIR"].Should().Be(_configDir);
    }

    [Fact]
    public void BuildLaunch_WithAProfile_MarksTheWorkingDirectoryTrustedBeforeReturning()
    {
        var provider = _CreateProvider(new CockpitOptions { Claude = new ClaudeCliOptions { WorkingDirectory = Directory.GetCurrentDirectory() } });
        var profile = new SessionProfile("Personal", new ClaudeConfig(_configDir));

        provider.BuildLaunch(Context(profile, workingDirectory: "/some/other/dir"));

        var claudeJson = File.ReadAllText(Path.Combine(_configDir, ".claude.json"));
        claudeJson.Should().Contain(Path.GetFullPath(Directory.GetCurrentDirectory()).Replace("\\", "\\\\"));
    }

    [Fact]
    public void BuildLaunch_WithAConfiguredCliWorkingDirectory_OverridesTheContextsWorkingDirectory()
    {
        var provider = _CreateProvider(new CockpitOptions { Claude = new ClaudeCliOptions { WorkingDirectory = Directory.GetCurrentDirectory() } });

        var spec = provider.BuildLaunch(Context(profile: null, workingDirectory: "/some/other/dir"));

        spec.WorkingDirectory.Should().Be(Path.GetFullPath(Directory.GetCurrentDirectory()));
    }

    [Fact]
    public void BuildLaunch_WithoutAConfiguredCliWorkingDirectory_UsesTheContextsWorkingDirectory()
    {
        var provider = _CreateProvider();
        var workingDirectory = Path.GetFullPath("/some/other/dir");

        var spec = provider.BuildLaunch(Context(profile: null, workingDirectory: workingDirectory));

        spec.WorkingDirectory.Should().Be(workingDirectory);
    }

    [Fact]
    public void BuildLaunch_WithoutAProfile_FallsBackToTheBundledOrConfiguredExecutable()
    {
        var executableLocator = Substitute.For<IClaudeExecutableLocator>();
        executableLocator.FindBundledExecutable().Returns("/opt/claude/claude");
        var provider = _CreateProvider(executableLocator: executableLocator);

        var spec = provider.BuildLaunch(Context(profile: null));

        spec.ExecutablePath.Should().Be("/opt/claude/claude");
    }

    [Fact]
    public void BuildLaunch_WithAnExecutablePathOnTheProfile_UsesItOverTheBundledOrConfiguredExecutable()
    {
        var executableLocator = Substitute.For<IClaudeExecutableLocator>();
        executableLocator.FindBundledExecutable().Returns("/opt/claude/claude");
        var provider = _CreateProvider(executableLocator: executableLocator);
        var profile = new SessionProfile("Personal", new ClaudeConfig(_configDir, "/usr/bin/claude"));

        var spec = provider.BuildLaunch(Context(profile));

        spec.ExecutablePath.Should().Be("/usr/bin/claude");
    }

    [Fact]
    public void BuildLaunch_WithOptions_BuildsTheArgumentsInOrder()
    {
        var provider = _CreateProvider();
        var profile = new SessionProfile("Personal", new ClaudeConfig(_configDir, "/usr/bin/claude"));
        var options = new Dictionary<string, string>
        {
            [TtyLaunchOption.PermissionMode] = "acceptEdits",
            [TtyLaunchOption.Model] = "opus",
            [TtyLaunchOption.Effort] = "xhigh",
        };

        var spec = provider.BuildLaunch(Context(profile, options));

        spec.Arguments.Should().Equal("--permission-mode", "acceptEdits", "--model", "opus", "--effort", "xhigh");
    }

    [Fact]
    public void BuildLaunch_NeverForcesASessionId_SoTheCliPersistsATranscriptTheTailerCanFind()
    {
        var provider = _CreateProvider();

        var spec = provider.BuildLaunch(Context(profile: null));

        spec.Arguments.Should().NotContain("--session-id");
    }

    public void Dispose() => Directory.Delete(_configDir, recursive: true);
}
