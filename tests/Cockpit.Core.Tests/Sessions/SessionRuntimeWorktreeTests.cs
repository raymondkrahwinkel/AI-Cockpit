using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Worktrees;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// The session-start worktree hook (AC-85, F2): a session started with isolation gets its own worktree and the
/// driver is started with that as its working directory, against a real git repository — the property is about
/// what the session's process actually runs in, so a fake git would prove nothing. Isolation is a per-session
/// choice passed to <see cref="ISessionRuntime.StartAsync"/>, not a profile setting.
/// </summary>
public sealed class SessionRuntimeWorktreeTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"cockpit-session-worktree-{Guid.NewGuid():n}");
    private readonly string _repo;
    private readonly WorktreeManager _worktreeManager;

    public SessionRuntimeWorktreeTests()
    {
        _repo = Path.Combine(_tempRoot, "repo");
        var worktreesRoot = Path.Combine(_tempRoot, "worktrees");
        var configPath = Path.Combine(_tempRoot, "cockpit.json");

        Directory.CreateDirectory(_repo);
        _Git(_repo, "init", "-b", "main");
        _Git(_repo, "config", "user.email", "test@example.com");
        _Git(_repo, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        _Git(_repo, "add", "-A");
        _Git(_repo, "commit", "-m", "first");

        _worktreeManager = new WorktreeManager(new WorktreeRegistryStore(configPath), worktreesRoot);
    }

    [Fact]
    public async Task StartAsync_IsolationRequestedOnARepo_RunsTheDriverInAFreshWorktree()
    {
        var driver = _Driver();
        await using var runtime = new SessionRuntime(_FactoryFor(driver), profile: null, _worktreeManager);
        var profile = _Profile("worktree-profile");

        await runtime.StartAsync(profile, workingDirectory: _repo, isolateInWorktree: true);

        runtime.Worktree.Should().NotBeNull();
        Directory.Exists(runtime.Worktree!.Path).Should().BeTrue();
        runtime.Worktree.Path.Should().NotBe(Path.GetFullPath(_repo));
        _Git(_repo, "branch", "--list", runtime.Worktree.Branch).Should().NotBeEmpty();
        await driver.Received(1).StartAsync(
            Arg.Any<SessionProfile?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlySet<string>?>(),
            Arg.Is<string?>(directory => directory == runtime.Worktree.Path),
            Arg.Any<SessionResume?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_IsolationNotRequested_RunsTheDriverInTheFolderAsGiven()
    {
        var driver = _Driver();
        await using var runtime = new SessionRuntime(_FactoryFor(driver), profile: null, _worktreeManager);
        var profile = _Profile("plain-profile");

        await runtime.StartAsync(profile, workingDirectory: _repo);

        runtime.Worktree.Should().BeNull();
        await driver.Received(1).StartAsync(
            Arg.Any<SessionProfile?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlySet<string>?>(),
            Arg.Is<string?>(directory => directory == _repo),
            Arg.Any<SessionResume?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_IsolationRequestedButFolderIsNotARepo_RunsInTheFolderWithoutIsolating()
    {
        var plain = Path.Combine(_tempRoot, "plain");
        Directory.CreateDirectory(plain);
        var driver = _Driver();
        await using var runtime = new SessionRuntime(_FactoryFor(driver), profile: null, _worktreeManager);
        var profile = _Profile("worktree-profile");

        await runtime.StartAsync(profile, workingDirectory: plain, isolateInWorktree: true);

        // Opt-in on a non-repository folder is not a silent isolated run: no worktree, and the driver runs in the
        // folder as given. The absent worktree (and, in the UI, the absent branch chip) is what says so (§4).
        runtime.Worktree.Should().BeNull();
        await driver.Received(1).StartAsync(
            Arg.Any<SessionProfile?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<IReadOnlySet<string>?>(),
            Arg.Is<string?>(directory => directory == plain),
            Arg.Any<SessionResume?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<CancellationToken>());
    }

    private static SessionProfile _Profile(string label) =>
        new(label, new OllamaConfig("http://localhost:11434", "llama3.1"));

    private static ISessionDriver _Driver()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_ => _NoEvents());
        return driver;
    }

    private static async IAsyncEnumerable<SessionEvent> _NoEvents([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    private static ISessionDriverFactory _FactoryFor(ISessionDriver driver)
    {
        var factory = Substitute.For<ISessionDriverFactory>();
        factory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return factory;
    }

    private static string _Git(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git did not start.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {standardError.Trim()}");
        }

        return standardOutput.Trim();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
