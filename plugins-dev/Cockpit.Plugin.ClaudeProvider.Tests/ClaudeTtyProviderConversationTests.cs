using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider.Tests;

/// <summary>
/// <see cref="ClaudeTtyProvider.WatchConversationIdAsync"/> (AC-408): a bounded, one-shot scan for the transcript
/// file this session's own launch created. Reports it exactly once, reports nothing when more than one new file
/// shows up in the same poll (an unattributable race with another session starting under the same config dir),
/// and gives up after its timeout instead of watching indefinitely — a standing watch would eventually see and
/// misreport a later, unrelated session's transcript as this session's own.
/// </summary>
public class ClaudeTtyProviderConversationTests : IDisposable
{
    private readonly string _stateDirectory = Directory.CreateTempSubdirectory("cockpit-claude-tty-conversation-tests-").FullName;

    private static readonly IReadOnlySet<string> NoBaseline = new HashSet<string>();

    private static readonly TimeSpan FastPollInterval = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task WatchConversationIdAsync_ReportsTheOnlyNewTranscript_Once()
    {
        var projectDir = _CreateProjectDir();
        var reported = new List<PluginConversationId>();

        var watch = ClaudeTtyProvider.WatchConversationIdAsync(
            _stateDirectory, NoBaseline, conversation => reported.Add(conversation),
            pollInterval: FastPollInterval, timeout: TimeSpan.FromSeconds(2));

        await Task.Delay(60);
        File.WriteAllText(Path.Combine(projectDir, "session-a.jsonl"), string.Empty);

        await watch;

        Assert.Equal([PluginConversationId.Known("session-a")], reported);
    }

    [Fact]
    public async Task WatchConversationIdAsync_ReportsNothing_WhenMoreThanOneNewTranscriptAppearsInTheSamePoll()
    {
        var projectDir = _CreateProjectDir();
        var reported = new List<PluginConversationId>();

        var watch = ClaudeTtyProvider.WatchConversationIdAsync(
            _stateDirectory, NoBaseline, conversation => reported.Add(conversation),
            pollInterval: FastPollInterval, timeout: TimeSpan.FromSeconds(2));

        // Two sessions' transcripts appear before the first poll notices either — an unattributable race the
        // watch must not resolve by guessing the newest one.
        await Task.Delay(60);
        File.WriteAllText(Path.Combine(projectDir, "session-a.jsonl"), string.Empty);
        File.WriteAllText(Path.Combine(projectDir, "session-b.jsonl"), string.Empty);

        await watch;

        Assert.Empty(reported);
    }

    [Fact]
    public async Task WatchConversationIdAsync_ReportsNothing_WhenNoTranscriptAppearsBeforeTheTimeout()
    {
        _CreateProjectDir();
        var reported = new List<PluginConversationId>();

        await ClaudeTtyProvider.WatchConversationIdAsync(
            _stateDirectory, NoBaseline, conversation => reported.Add(conversation),
            pollInterval: FastPollInterval, timeout: TimeSpan.FromMilliseconds(100));

        Assert.Empty(reported);
    }

    /// <summary>
    /// The stand-in for a <c>/clear</c>: a second transcript appears well after the first was already reported.
    /// This TTY route deliberately does not catch it (see the method's remarks) — the watch has already stopped
    /// by the time the second file exists, which is the accepted trade-off, not a bug.
    /// </summary>
    [Fact]
    public async Task WatchConversationIdAsync_DoesNotReportAgain_ForATranscriptThatAppearsAfterItAlreadyReported()
    {
        var projectDir = _CreateProjectDir();
        var reported = new List<PluginConversationId>();

        var watch = ClaudeTtyProvider.WatchConversationIdAsync(
            _stateDirectory, NoBaseline, conversation => reported.Add(conversation),
            pollInterval: FastPollInterval, timeout: TimeSpan.FromSeconds(2));

        await Task.Delay(60);
        File.WriteAllText(Path.Combine(projectDir, "session-a.jsonl"), string.Empty);
        await watch;

        File.WriteAllText(Path.Combine(projectDir, "session-b.jsonl"), string.Empty);
        await Task.Delay(200);

        Assert.Equal([PluginConversationId.Known("session-a")], reported);
    }

    [Fact]
    public void BuildLaunch_WithNoReportConversationIdCallback_DoesNotThrow()
    {
        var context = new PluginTtyLaunchContext(_ConfigJson(), new Dictionary<string, string>(), "/tmp/workdir", null, new Dictionary<string, string>());

        var spec = new ClaudeTtyProvider().BuildLaunch(context);

        Assert.NotNull(spec);
    }

    [Fact]
    public void BuildLaunch_WithACallback_ReturnsTheLaunchSpecImmediately_WithoutWaitingForTheTranscript()
    {
        var context = new PluginTtyLaunchContext(_ConfigJson(), new Dictionary<string, string>(), "/tmp/workdir", null, new Dictionary<string, string>())
        {
            ReportConversationId = _ => { },
        };

        var spec = new ClaudeTtyProvider().BuildLaunch(context);

        Assert.NotNull(spec);
    }

    private string _CreateProjectDir()
    {
        var projectDir = Path.Combine(_stateDirectory, "projects", "some-cwd-hash");
        Directory.CreateDirectory(projectDir);
        return projectDir;
    }

    private string _ConfigJson() =>
        JsonSerializer.Serialize(new ClaudeProviderConfig(ConfigDir: _stateDirectory), ClaudeProviderConfig.JsonOptions);

    public void Dispose() => Directory.Delete(_stateDirectory, recursive: true);
}
