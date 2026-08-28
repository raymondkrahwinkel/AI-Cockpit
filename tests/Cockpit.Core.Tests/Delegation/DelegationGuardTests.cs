using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Delegation;
using Cockpit.Core.Mcp;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Delegation;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Delegation;

/// <summary>
/// Delegation (#67) spawns a real process under someone else's profile on the say-so of a model, so the guards
/// are the feature. These tests are about what the engine <em>refuses</em>: a profile that never opted in, a task
/// type it does not take, a working directory outside what it allows, a delegated task delegating on, and a
/// caller trying to run more at once than the profile's usage pot can carry.
/// </summary>
public class DelegationGuardTests
{
    [Fact]
    public async Task DelegateAsync_ToAProfileThatIsNotATarget_IsRefused()
    {
        // The default: a profile is not a delegation target until someone opts it in by hand.
        var service = _ServiceWith(new SessionProfile("private", new ClaudeConfig("/home/raymond/.claude")));

        var delegate_ = async () => await service.DelegateAsync(new DelegationRequest("private", "do work"));

        var thrown = await Assert.ThrowsAsync<DelegationRejectedException>(delegate_);
        Assert.Contains("not available as a delegation target", thrown.Message);
    }

    [Fact]
    public async Task DelegateAsync_ToAnUnknownProfile_IsRefused()
    {
        // Only an existing profile can be a target: the driver, credentials and environment come from the
        // profile, so a free-form target would be a way to run anything.
        var service = _ServiceWith(_Target("local"));

        var delegate_ = async () => await service.DelegateAsync(new DelegationRequest("no-such-profile", "do work"));

        var thrown = await Assert.ThrowsAsync<DelegationRejectedException>(delegate_);
        Assert.Contains("No profile named", thrown.Message);
    }

    [Fact]
    public async Task DelegateAsync_WithATaskTypeTheProfileDoesNotAccept_IsRefused()
    {
        var service = _ServiceWith(_Target("local", policy => policy with { AllowedTaskTypes = ["summarize"] }));

        var delegate_ = async () => await service.DelegateAsync(new DelegationRequest("local", "rm -rf", TaskType: "refactor"));

        var thrown = await Assert.ThrowsAsync<DelegationRejectedException>(delegate_);
        Assert.Contains("only accepts these task types", thrown.Message);
    }

    [Fact]
    public async Task DelegateAsync_WithAWorkingDirectoryOutsideThePolicy_IsRefused()
    {
        var service = _ServiceWith(_Target("local", policy => policy with { AllowedWorkingDirs = ["/home/raymond/projects"] }));

        var delegate_ = async () => await service.DelegateAsync(
            new DelegationRequest("local", "read the secrets", WorkingDirectory: "/etc"));

        var thrown = await Assert.ThrowsAsync<DelegationRejectedException>(delegate_);
        Assert.Contains("does not allow a task to run in", thrown.Message);
    }

    [Fact]
    public async Task DelegateAsync_CannotWalkOutOfAnAllowedWorkingDirectory()
    {
        // The check resolves the path first, so a traversal that lands outside the allowed root is still outside.
        var service = _ServiceWith(_Target("local", policy => policy with { AllowedWorkingDirs = ["/home/raymond/projects"] }));

        var delegate_ = async () => await service.DelegateAsync(
            new DelegationRequest("local", "escape", WorkingDirectory: "/home/raymond/projects/../../.ssh"));

        await Assert.ThrowsAsync<DelegationRejectedException>(delegate_);
    }

    // AC-1160: the allow-list compared `OrdinalIgnoreCase` on every platform, so where the filesystem keeps
    // `repo` and `Repo` apart the guard still called them one root. What is asserted here is not a platform but
    // what the volume under the temp directory actually does -- the two answers are not the same question.
    [Fact]
    public async Task DelegateAsync_WithAWorkingDirectoryDifferingOnlyInCase_FollowsTheVolume()
    {
        var root = _TempRoot();
        try
        {
            var allowed = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
            var asked = Path.Combine(root, "Repo");
            var service = _ServiceWith(_Target("local", policy => policy with { AllowedWorkingDirs = [allowed] }));

            var delegate_ = async () => await service.DelegateAsync(
                new DelegationRequest("local", "work", WorkingDirectory: asked));

            if (_FoldsCase(root))
            {
                // The negative control: where the filesystem itself says these are one directory, the guard has to
                // go on allowing it. This fix may not cost a Windows or macOS operator a directory that works.
                Assert.NotEqual(DelegatedTaskStatus.Failed, (await delegate_()).Status);
                return;
            }

            await Assert.ThrowsAsync<DelegationRejectedException>(delegate_);
        }
        finally
        {
            _Discard(root);
        }
    }

    // The same question on a volume that is made to keep the two apart, so the refusing half is covered on a
    // machine that is not Linux: NTFS carries case sensitivity per directory, and `fsutil` sets it.
    [Fact]
    public async Task DelegateAsync_WithAWorkingDirectoryDifferingOnlyInCase_IsRefused_WhereTheVolumeKeepsThemApart()
    {
        var root = _TempRoot();
        try
        {
            _MakeCaseSensitive(root);
            Assert.False(
                _FoldsCase(root),
                $"This test needs a directory whose volume keeps two spellings apart, and '{root}' folds them. " +
                "It has not been measured here rather than having passed.");

            var allowed = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;

            // The shape the ticket describes: `Repo` is a real directory of its own, and it is not the one the
            // operator allowed. An entry spelled exactly as asked is never the differently-cased sibling.
            var asked = Directory.CreateDirectory(Path.Combine(root, "Repo")).FullName;
            var service = _ServiceWith(_Target("local", policy => policy with { AllowedWorkingDirs = [allowed] }));

            var delegate_ = async () => await service.DelegateAsync(
                new DelegationRequest("local", "work", WorkingDirectory: asked));

            var thrown = await Assert.ThrowsAsync<DelegationRejectedException>(delegate_);
            Assert.Contains("does not allow a task to run in", thrown.Message);
        }
        finally
        {
            _Discard(root);
        }
    }

    // AC-1160: `Path.GetFullPath` canonicalises lexically, so a link under an allowed root used to be inside it
    // by spelling while the process landed outside it. The second half is the one .NET will not do for you: it
    // resolves a link that is the last segment and returns null for one halfway up a path.
    [Fact]
    public async Task DelegateAsync_ThroughASymlinkPointingOutOfAnAllowedRoot_IsRefused()
    {
        var root = _TempRoot();
        try
        {
            var allowed = Directory.CreateDirectory(Path.Combine(root, "allowed")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            Directory.CreateDirectory(Path.Combine(outside, "sub"));
            var link = Path.Combine(allowed, "link");
            Directory.CreateSymbolicLink(link, outside);

            var service = _ServiceWith(_Target("local", policy => policy with { AllowedWorkingDirs = [allowed] }));

            var throughTheLink = async () => await service.DelegateAsync(
                new DelegationRequest("local", "escape", WorkingDirectory: link));
            var pastTheLink = async () => await service.DelegateAsync(
                new DelegationRequest("local", "escape", WorkingDirectory: Path.Combine(link, "sub")));

            Assert.Contains("does not allow a task to run in", (await Assert.ThrowsAsync<DelegationRejectedException>(throughTheLink)).Message);
            Assert.Contains("does not allow a task to run in", (await Assert.ThrowsAsync<DelegationRejectedException>(pastTheLink)).Message);
        }
        finally
        {
            _Discard(root);
        }
    }

    // AC-1160: a chain of links that never comes to rest has to refuse, not be judged on how far it got. Links
    // that stay inside the allowed root until one past the budget leaves it would otherwise be waved through on
    // the spelling at that point, while the OS follows the whole chain at spawn time and lands outside.
    [Fact]
    public async Task DelegateAsync_ThroughALinkChainLongerThanTheHopBudget_IsRefused()
    {
        var root = _TempRoot();
        try
        {
            var allowed = Directory.CreateDirectory(Path.Combine(root, "allowed")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;

            const int Links = 41;
            for (var index = 0; index < Links; index++)
            {
                var step = Directory.CreateDirectory(Path.Combine(allowed, $"d{index}")).FullName;
                Directory.CreateSymbolicLink(
                    Path.Combine(step, "l"),
                    index == Links - 1 ? outside : Path.Combine(allowed, $"d{index + 1}"));
            }

            var asked = Path.Combine(
                allowed,
                "d0",
                string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("l", Links)));
            var service = _ServiceWith(_Target("local", policy => policy with { AllowedWorkingDirs = [allowed] }));

            var delegate_ = async () => await service.DelegateAsync(
                new DelegationRequest("local", "escape", WorkingDirectory: asked));

            var thrown = await Assert.ThrowsAsync<DelegationRejectedException>(delegate_);
            Assert.Contains("does not allow a task to run in", thrown.Message);
        }
        finally
        {
            _Discard(root);
        }
    }

    // The same rule at the seam, both ways round. The budget counts passes rather than links, and the last pass
    // is the one that finds no further link -- so a two-link chain settles on the third and not the second.
    [Fact]
    public void Canonicalize_ResolvesAChainWithinItsBudget_AndYieldsNothingBeyondIt()
    {
        var root = _TempRoot();
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(root, "d0")).FullName;
            var second = Directory.CreateDirectory(Path.Combine(root, "d1")).FullName;
            var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(first, "l"), second);
            Directory.CreateSymbolicLink(Path.Combine(second, "l"), target);

            var asked = Path.Combine(first, "l", "l");

            Assert.Equal(target, FilesystemPath.Canonicalize(asked, maxLinkHops: 3));
            Assert.Null(FilesystemPath.Canonicalize(asked, maxLinkHops: 2));
        }
        finally
        {
            _Discard(root);
        }
    }

    private static string _TempRoot([CallerMemberName] string name = "")
    {
        var root = Path.Combine(Path.GetTempPath(), $"ac1160-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    // Asked of the directory rather than of the operating system, because the answer belongs to the volume: a
    // case-insensitive mount on Linux and a case-sensitive volume on macOS both exist.
    private static bool _FoldsCase(string directory)
    {
        var probe = Path.Combine(directory, $"probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(probe);
        try
        {
            return Directory.Exists(probe.ToUpperInvariant());
        }
        finally
        {
            Directory.Delete(probe);
        }
    }

    // Windows only, and only on NTFS: the flag is per directory and children inherit it. Elsewhere the default
    // already keeps spellings apart, and the caller's own assertion is what says whether that held.
    private static void _MakeCaseSensitive(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fsutil = Process.Start(new ProcessStartInfo("fsutil", $"file setCaseSensitiveInfo \"{directory}\" enable")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        fsutil.WaitForExit();
    }

    private static void _Discard(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test that already made its point.
        }
    }

    [Fact]
    public async Task DelegateAsync_FromADelegatedTask_IsRefused_UnlessTheProfileAllowsIt()
    {
        // Without this a sub-agent handed the orchestrator tools could delegate in a loop.
        var service = _ServiceWith(_Target("local"));

        var delegate_ = async () => await service.DelegateAsync(new DelegationRequest("local", "and again", Depth: 1));

        var thrown = await Assert.ThrowsAsync<DelegationRejectedException>(delegate_);
        Assert.Contains("may not delegate further", thrown.Message);
    }

    [Fact]
    public async Task DelegateAsync_BeyondTheProfilesConcurrencyCap_QueuesRatherThanSpawning()
    {
        // The cap protects the provider's usage pot. The honest answer is "queued" — not a silent refusal, and
        // certainly not starting it anyway.
        var service = _ServiceWith(_Target("local", policy => policy with { MaxConcurrent = 1 }));

        var first = await service.DelegateAsync(new DelegationRequest("local", "first"));
        var second = await service.DelegateAsync(new DelegationRequest("local", "second"));

        Assert.Equal(DelegatedTaskStatus.Running, first.Status);
        Assert.Equal(DelegatedTaskStatus.Queued, second.Status);
    }

    [Fact]
    public async Task ListTargetsAsync_HidesProfilesThatAreNotTargets()
    {
        // An agent cannot delegate to what it cannot see; the opted-out profile is simply absent.
        var service = _ServiceWith(
            new SessionProfile("private", new ClaudeConfig("/home/raymond/.claude")),
            _Target("local", policy => policy with { Purpose = "cheap bulk work", Tags = ["local", "cheap"] }));

        var targets = await service.ListTargetsAsync();

        Assert.Single(targets);
        Assert.Equal("local", targets[0].ProfileLabel);
        Assert.Equal("cheap bulk work", targets[0].Purpose);
        Assert.Contains("cheap", targets[0].Tags);
    }

    [Fact]
    public async Task StartedTask_RunsUnderTheProfilesPermissionCeiling_NotWhateverTheCallerWanted()
    {
        // A delegated session has nobody to answer a permission prompt, so it must not run in a mode that waits
        // for one — and "non-interactive" must not quietly become "bypass everything".
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver, _Target("local", policy => policy with { PermissionCeiling = "plan" }));

        await service.DelegateAsync(new DelegationRequest("local", "look around"));

        await driver.Received(1).StartAsync(
            Arg.Any<SessionProfile?>(),
            "plan",
            Arg.Any<string?>(),
            Arg.Any<IReadOnlySet<string>?>(),
            Arg.Any<string?>(),
            Arg.Any<SessionResume?>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartedTask_WithoutAutoApprove_InstallsTheCeilingGate_NotBlanketAutoApprove()
    {
        // AC-79: a delegated local-model session is non-interactive (no human to answer a tool prompt). With the
        // profile's "Auto-Approve tool calls" off, it must gate each tool call against the ceiling + allow-list
        // rather than either hanging on a prompt or blanket-approving everything. _Target's default ceiling is
        // acceptEdits and it lists no tools.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver, _Target("local", policy => policy with { PermissionCeiling = "plan", AllowedTools = ["get_current_user"] }));

        await service.DelegateAsync(new DelegationRequest("local", "call a tool"));

        await driver.Received(1).SetDelegatedToolGateAsync(
            "plan",
            Arg.Is<IReadOnlyList<string>>(list => list.Count == 1 && list.Contains("get_current_user")),
            Arg.Any<CancellationToken>());
        await driver.DidNotReceive().SetAutoApproveToolsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartedTask_WithoutAutoApprove_UsesTheReadOnlyDefault_NotTheProfileCeiling()
    {
        // AC-971: the default target's ceiling (acceptEdits) is what it MAY allow, not what a task gets for free.
        // A task whose caller asked for no permission is gated read-only, and no allow-list widens that.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver, _Target("local"));

        await service.DelegateAsync(new DelegationRequest("local", "call a tool"));

        await driver.Received(1).SetDelegatedToolGateAsync(
            DelegatedToolPermissionPolicy.ReadOnlyCeiling,
            Arg.Is<IReadOnlyList<string>>(list => list.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartedTask_WithAutoApproveOn_AllowsEverything_AndDoesNotInstallTheCeilingGate()
    {
        // The operator's per-profile "Auto-Approve tool calls" is the explicit "trust this profile fully": a
        // delegated session then allows every tool (still bounded by the enabled-server set), so it uses blanket
        // auto-approve and not the ceiling gate.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var profile = new SessionProfile(
            "local",
            new ClaudeConfig(string.Empty),
            Defaults: new ProfileDefaults(string.Empty, string.Empty, string.Empty, AutoApproveTools: true),
            Delegation: new DelegationPolicy(AllowedAsTarget: true));
        var service = _ServiceWith(driver, profile);

        await service.DelegateAsync(new DelegationRequest("local", "call a tool"));

        await driver.Received(1).SetAutoApproveToolsAsync(true, Arg.Any<CancellationToken>());
        await driver.DidNotReceive().SetDelegatedToolGateAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartedTask_SendsThePromptToTheSession()
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var service = _ServiceWith(driver, _Target("local"));

        var task = await service.DelegateAsync(new DelegationRequest("local", "summarise the changelog"));

        await driver.Received(1).SendUserMessageAsync(
            "summarise the changelog",
            Arg.Any<IReadOnlyList<Cockpit.Core.Sessions.ImageAttachment>?>(),
            Arg.Any<CancellationToken>());
        Assert.Equal(DelegatedTaskStatus.Running, service.GetTask(task.TaskId)!.Status);
    }

    [Fact]
    public async Task ADelegatedSession_KeepsItsOwnTools_ButNotTheOrchestrator()
    {
        // A sub-agent still needs its files, its shell, its git — withholding those would make delegation
        // useless. What it does not get is the orchestrator itself, so it cannot hand work on and start a chain.
        // This is the second lock on the recursion guard: no delegate_task tool, no chain, even if the depth
        // check were wrong.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var service = _Service(driverFactory, _Registry(), _Target("local"));

        await service.DelegateAsync(new DelegationRequest("local", "work"));

        await driver.Received(1).StartAsync(
            Arg.Any<SessionProfile?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<IReadOnlySet<string>?>(servers =>
                servers!.Contains("filesystem") && !servers.Contains("cockpit-orchestrator")),
            Arg.Any<string?>(),
            Arg.Any<SessionResume?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADelegatedSession_GetsTheOrchestrator_WhenItsProfileMayDelegateFurther()
    {
        // The escape hatch is explicit and per profile: turn it on and that profile's tasks can delegate on.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var service = _Service(
            driverFactory,
            _Registry(),
            _Target("local", policy => policy with { MayDelegateFurther = true }));

        await service.DelegateAsync(new DelegationRequest("local", "work"));

        await driver.Received(1).StartAsync(
            Arg.Any<SessionProfile?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<IReadOnlySet<string>?>(servers => servers!.Contains("cockpit-orchestrator")),
            Arg.Any<string?>(),
            Arg.Any<SessionResume?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendFollowUp_ReachesATaskThatHasAlreadyAnswered()
    {
        // A task that answered is Completed but its session is deliberately kept alive, so a follow-up must land
        // on it. This was broken: "finished" was read as "cannot take another turn", the message was dropped, and
        // the caller got a response that looked like success — so it waited for a turn that was never coming.
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_StreamCompletingATurn());
        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        var service = _Service(driverFactory, _Registry(), _Target("local"));

        var task = await service.DelegateAsync(new DelegationRequest("local", "first turn"));
        await _WaitUntilAsync(() => service.GetTask(task.TaskId)!.Status == DelegatedTaskStatus.Completed);

        // The whole point is the follow-up landing on an *answered* task, so assert that state before sending —
        // without this the test passes against the bug simply because the turn had not completed yet.
        Assert.Equal(DelegatedTaskStatus.Completed, service.GetTask(task.TaskId)!.Status);

        await service.SendFollowUpAsync(task.TaskId, "and now the tests");

        await driver.Received(1).SendUserMessageAsync("and now the tests", Arg.Any<IReadOnlyList<Cockpit.Core.Sessions.ImageAttachment>?>(), Arg.Any<CancellationToken>());
        Assert.Equal(DelegatedTaskStatus.Running, service.GetTask(task.TaskId)!.Status);
    }

    [Fact]
    public async Task SendFollowUp_WhenTheProfileIsAlreadyAtItsCap_IsRefused()
    {
        // Found in live use: the cap gated new tasks but not follow-ups, so a follow-up woke a finished session
        // back up alongside a task that was already running on a profile set to one at a time — two models on the
        // same GPU, two draws on the same usage pot. The cap counts work being done, not tasks being started.
        // The first session answers and stays alive (so it could take a follow-up); the second keeps working, and
        // is therefore the profile's one allowed running task.
        var answering = Substitute.For<ISessionDriver>();
        answering.Events.Returns(_StreamCompletingATurn());
        var stillWorking = Substitute.For<ISessionDriver>();
        stillWorking.Events.Returns(_StreamThatNeverFinishes());

        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(answering, stillWorking);
        var service = _Service(driverFactory, _Registry(), _Target("local", policy => policy with { MaxConcurrent = 1 }));

        var first = await service.DelegateAsync(new DelegationRequest("local", "first"));
        await _WaitUntilAsync(() => service.GetTask(first.TaskId)!.Status == DelegatedTaskStatus.Completed);

        var second = await service.DelegateAsync(new DelegationRequest("local", "second"));
        Assert.Equal(DelegatedTaskStatus.Running, service.GetTask(second.TaskId)!.Status);

        var followUp = async () => await service.SendFollowUpAsync(first.TaskId, "one more thing");

        var thrown = await Assert.ThrowsAsync<DelegationRejectedException>(followUp);
        Assert.Contains("already running as many tasks as it allows", thrown.Message);
        Assert.Equal(DelegatedTaskStatus.Completed, service.GetTask(first.TaskId)!.Status);
    }

    [Fact]
    public async Task SendFollowUp_ToATaskWhoseSessionIsGone_IsRefusedLoudly()
    {
        // The other half: never a quiet "ok" for a follow-up that cannot land.
        var service = _ServiceWith(_Target("local"));
        var task = await service.DelegateAsync(new DelegationRequest("local", "work"));
        await service.StopAsync(task.TaskId);

        var followUp = async () => await service.SendFollowUpAsync(task.TaskId, "more please");

        var thrown = await Assert.ThrowsAsync<DelegationRejectedException>(followUp);
        Assert.Contains("no live session", thrown.Message);
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 50 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
    }

    // A session that is still working: it has produced nothing yet and its stream stays open, so the task sits at
    // Running — which is what occupies the profile's slot.
    private static async IAsyncEnumerable<Cockpit.Core.Sessions.SessionEvent> _StreamThatNeverFinishes()
    {
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
        yield break;
    }

    private static async IAsyncEnumerable<Cockpit.Core.Sessions.SessionEvent> _StreamCompletingATurn()
    {
        yield return new Cockpit.Core.Sessions.AssistantTextCompleted { SessionId = "s1", Text = "here you go" };
        yield return new Cockpit.Core.Sessions.TurnCompleted { SessionId = "s1", Subtype = "success", Result = null, IsError = false };

        // The session stays open after the turn, exactly as a real driver's stream does while it waits for the
        // next message — if this completed, the runtime would look dead and the follow-up would have nowhere to go.
        await Task.Delay(Timeout.Infinite, CancellationToken.None);
    }

    [Fact]
    public async Task AFailingStart_MarksTheTaskFailed_RatherThanLeavingItQueuedForever()
    {
        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(_ => throw new InvalidOperationException("no such plugin provider"));
        var service = _Service(driverFactory, _Target("local"));

        var task = await service.DelegateAsync(new DelegationRequest("local", "work"));

        Assert.Equal(DelegatedTaskStatus.Failed, task.Status);
        Assert.Contains("no such plugin provider", task.Error);
    }

    private static SessionProfile _Target(string label, Func<DelegationPolicy, DelegationPolicy>? tune = null)
    {
        var policy = new DelegationPolicy(AllowedAsTarget: true);
        return new SessionProfile(label, new ClaudeConfig(string.Empty), Delegation: tune?.Invoke(policy) ?? policy);
    }

    private static DelegationService _ServiceWith(params SessionProfile[] profiles)
    {
        var driver = Substitute.For<ISessionDriver>();
        driver.Events.Returns(_EmptyStream());
        return _ServiceWith(driver, profiles);
    }

    private static DelegationService _ServiceWith(ISessionDriver driver, params SessionProfile[] profiles)
    {
        var driverFactory = Substitute.For<ISessionDriverFactory>();
        driverFactory.Create(Arg.Any<SessionProfile?>()).Returns(driver);
        return _Service(driverFactory, profiles);
    }

    private static DelegationService _Service(ISessionDriverFactory driverFactory, params SessionProfile[] profiles) =>
        _Service(driverFactory, _Registry(), profiles);

    private static DelegationService _Service(
        ISessionDriverFactory driverFactory,
        IMcpServerStore mcpServerStore,
        params SessionProfile[] profiles)
    {
        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(profiles);
        return new DelegationService(profileStore, new SessionManager(driverFactory), mcpServerStore, Substitute.For<IDelegationAuditLog>(), NoSessionWorkspaces.Instance);
    }

    // The MCP registry as the operator configured it: their own servers, plus the orchestrator they switched on
    // for their main session.
    private static IMcpServerStore _Registry(params McpServerConfig[] servers)
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(servers.Length > 0
            ? servers
            : [
                new McpServerConfig { Name = "filesystem", Enabled = true },
                new McpServerConfig { Name = "cockpit-orchestrator", Enabled = true },
            ]);
        return store;
    }

    private static async IAsyncEnumerable<Cockpit.Core.Sessions.SessionEvent> _EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }
}
