using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Screenshots;
using Cockpit.Core.Toasts;
using Cockpit.Core.Voice;

namespace Cockpit.Core.Tests.Hotkeys;

/// <summary>
/// <see cref="GlobalHotkeyCoordinator"/> is the single point where the cockpit's desktop-wide keys are armed
/// (#34, AC-220): it reads what each feature wants and registers exactly that set. These are the arming rules
/// — which keys go to the OS, what happens when the settings cannot be read, and that a switched-off feature
/// contributes nothing.
/// </summary>
public class GlobalHotkeyCoordinatorTests
{
    [Fact]
    public async Task WithEveryFeatureOff_NothingIsRegisteredWithTheDesktop()
    {
        var service = new FakeGlobalHotkeyService();
        var coordinator = TestGlobalHotkeys.Coordinator(service);

        await coordinator.ApplyAsync();

        Assert.Empty(service.LastBindings);
        Assert.False(coordinator.IsArmed(GlobalHotkeys.PushToTalk));
        Assert.False(coordinator.IsArmed(GlobalHotkeys.Screenshot));
    }

    /// <summary>
    /// A key the operator switched on and the desktop refused says so (AC-332). It used to read exactly like a
    /// key nobody had asked for — an empty line, no error — and the shortcut simply did nothing when pressed,
    /// which is the silence this whole reporting path exists to prevent.
    /// </summary>
    [Fact]
    public async Task AKeyThatWasAskedForAndCouldNotBeArmed_SaysSoRatherThanNothing()
    {
        var service = new FakeGlobalHotkeyService { StartFailure = new InvalidOperationException("the desktop said no") };
        var coordinator = TestGlobalHotkeys.Coordinator(
            service, new VoiceSettings { IsEnabled = true, GlobalPushToTalk = true, PushToTalkKeyName = "F9" });

        await coordinator.ApplyAsync();

        Assert.False(coordinator.IsArmed(GlobalHotkeys.PushToTalk), "nothing was registered");
        Assert.Equal("could not be armed", _Describe(coordinator, GlobalHotkeys.PushToTalk));
    }

    /// <summary>
    /// And a key nobody asked for still says nothing, even when arming something else failed. Telling an operator
    /// their desktop refused a shortcut they never switched on sends them into their settings looking for nothing.
    /// </summary>
    [Fact]
    public async Task AKeyNobodyAskedFor_StillSaysNothing()
    {
        var service = new FakeGlobalHotkeyService { StartFailure = new InvalidOperationException("the desktop said no") };
        var coordinator = TestGlobalHotkeys.Coordinator(
            service, new VoiceSettings { IsEnabled = true, GlobalPushToTalk = true, PushToTalkKeyName = "F9" });

        await coordinator.ApplyAsync();

        Assert.Empty(_Describe(coordinator, GlobalHotkeys.Screenshot));
    }

    private static string _Describe(GlobalHotkeyCoordinator coordinator, string hotkeyId) =>
        coordinator.DescribeTrigger(hotkeyId, "unbound", "unsupported", "could not be armed");

    /// <summary>Voice switched on but the desktop-wide hold switched off is not a binding: the per-view local key covers it.</summary>
    [Fact]
    public async Task VoiceOnButGlobalPushToTalkOff_RegistersNoPushToTalkKey()
    {
        var service = new FakeGlobalHotkeyService();
        var coordinator = TestGlobalHotkeys.Coordinator(service, new VoiceSettings { IsEnabled = true, GlobalPushToTalk = false });

        await coordinator.ApplyAsync();

        Assert.Empty(service.LastBindings);
    }

    [Fact]
    public async Task EachSwitchedOnFeature_ContributesItsOwnKey()
    {
        var service = new FakeGlobalHotkeyService();
        var coordinator = TestGlobalHotkeys.Coordinator(
            service,
            new VoiceSettings { IsEnabled = true, GlobalPushToTalk = true, PushToTalkKeyName = "F9" },
            new ScreenshotSettings { GlobalHotkeyEnabled = true, HotkeyKeyName = "F8" });

        await coordinator.ApplyAsync();

        Assert.Collection(service.LastBindings,
            binding => Assert.Equivalent(new GlobalHotkeyBinding(GlobalHotkeys.PushToTalk, "Push to talk (hold)", "F9"), binding),
            binding => Assert.Equivalent(new GlobalHotkeyBinding(GlobalHotkeys.Screenshot, "Take a screenshot", "F8"), binding));
        Assert.True(coordinator.IsArmed(GlobalHotkeys.PushToTalk));
        Assert.True(coordinator.IsArmed(GlobalHotkeys.Screenshot));
    }

    /// <summary>
    /// The whole reason a second key could not simply arm itself: <see cref="IGlobalHotkeyService.StartAsync"/>
    /// registers a set, so two features each arming their own would leave only the last one working. One call,
    /// both keys.
    /// </summary>
    [Fact]
    public async Task BothKeysAreArmedInOneRegistration()
    {
        var service = new FakeGlobalHotkeyService();
        var coordinator = TestGlobalHotkeys.Coordinator(
            service, TestGlobalHotkeys.GlobalPushToTalkOn, new ScreenshotSettings { GlobalHotkeyEnabled = true });

        await coordinator.ApplyAsync();

        Assert.Equal(1, service.StartCallCount);
        Assert.Equal(2, System.Linq.Enumerable.Count(service.LastBindings));
    }

    /// <summary>
    /// The key was read once, at startup, and nothing re-armed: changing it in Options saved the new key and left
    /// the hook listening for the old one for the rest of the session, with nothing anywhere saying so. Raymond:
    /// "we kunnen de keybind niet aanpassen" — you could type it; it simply did nothing.
    /// </summary>
    [Fact]
    public async Task ApplyingAgain_ReArmsRatherThanLeavingTheOldKey()
    {
        var service = new FakeGlobalHotkeyService();
        var coordinator = TestGlobalHotkeys.Coordinator(service, TestGlobalHotkeys.GlobalPushToTalkOn);

        await coordinator.ApplyAsync();
        await coordinator.ApplyAsync();

        Assert.Equal(2, service.StartCallCount);
    }

    /// <summary>Re-arming must not double a hold: a second subscription on the same service means every press fires twice.</summary>
    [Fact]
    public async Task ReArming_DoesNotSubscribeTwice()
    {
        var service = new FakeGlobalHotkeyService();
        var coordinator = TestGlobalHotkeys.Coordinator(service, TestGlobalHotkeys.GlobalPushToTalkOn);

        await coordinator.ApplyAsync();
        await coordinator.ApplyAsync();

        Assert.Equal(1, service.PressedSubscriberCount);
    }

    /// <summary>
    /// Its callers discard the task (app startup, a settings save), so a throw here used to land on a task nobody
    /// observes and take the hotkey with it. On 2026-07-15 that happened for real: reading the voice settings hit
    /// <c>cockpit.json</c> while the plugin layer was writing it, and F9 was dead for the whole session with not
    /// one line in the log. It still cannot arm — but it has to say so.
    /// </summary>
    [Fact]
    public async Task WhenTheSettingsCannotBeRead_LogsIt_RatherThanDyingOnATaskNobodyObserves()
    {
        var voiceStore = Substitute.For<IVoiceSettingsStore>();
        voiceStore.LoadAsync(Arg.Any<CancellationToken>())
            .Returns<VoiceSettings>(_ => throw new IOException("cockpit.json is being used by another process"));
        var screenshotStore = Substitute.For<IScreenshotSettingsStore>();
        screenshotStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ScreenshotSettings());
        var logger = new CapturingLogger<GlobalHotkeyCoordinator>();
        var coordinator = new GlobalHotkeyCoordinator(
            new FakeGlobalHotkeyService(),
            voiceStore,
            screenshotStore,
            TestGlobalHotkeys.AlwaysAvailable(),
            Substitute.For<IToastService>(),
            logger);

        var act = async () => await coordinator.ApplyAsync();

        await act();
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is IOException);
    }

    /// <summary>
    /// A re-arm that fails must not leave the previous set standing. Saving a changed key goes through here, and
    /// on the failure path nothing is registered with the desktop any more — so a stale armed set would have the
    /// settings screen reporting a trigger for a key that no longer fires, which is worse than saying nothing.
    /// </summary>
    [Fact]
    public async Task AFailedReArmAfterASuccessfulOne_LeavesNothingCountingAsArmed()
    {
        var voiceStore = Substitute.For<IVoiceSettingsStore>();
        voiceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(
            _ => Task.FromResult(TestGlobalHotkeys.GlobalPushToTalkOn),
            _ => Task.FromException<VoiceSettings>(new IOException("cockpit.json is being used by another process")));
        var screenshotStore = Substitute.For<IScreenshotSettingsStore>();
        screenshotStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ScreenshotSettings());
        var coordinator = new GlobalHotkeyCoordinator(
            new FakeGlobalHotkeyService(),
            voiceStore,
            screenshotStore,
            TestGlobalHotkeys.AlwaysAvailable(),
            Substitute.For<IToastService>(),
            new CapturingLogger<GlobalHotkeyCoordinator>());

        await coordinator.ApplyAsync();
        Assert.True(coordinator.IsArmed(GlobalHotkeys.PushToTalk), "the first arm succeeded");

        await coordinator.ApplyAsync();

        Assert.False(coordinator.IsArmed(GlobalHotkeys.PushToTalk), "the re-arm failed, so nothing is registered any more");
    }

    [Fact]
    public async Task WhenTheServiceRefusesToArm_ItIsLogged_AndNothingCountsAsArmed()
    {
        var service = new FakeGlobalHotkeyService { StartFailure = new InvalidOperationException("no hook for you") };
        var logger = new CapturingLogger<GlobalHotkeyCoordinator>();
        var coordinator = TestGlobalHotkeys.Coordinator(service, TestGlobalHotkeys.GlobalPushToTalkOn, logger: logger);

        await coordinator.ApplyAsync();

        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.False(coordinator.IsArmed(GlobalHotkeys.PushToTalk));
    }

    /// <summary>
    /// Two features on one key: both backends have to pick one, so the operator is told rather than left with a
    /// feature that quietly stopped working the moment they typed the key.
    /// </summary>
    [Fact]
    public async Task TwoFeaturesOnTheSameKey_AreReportedAsAClash()
    {
        var logger = new CapturingLogger<GlobalHotkeyCoordinator>();
        var coordinator = TestGlobalHotkeys.Coordinator(
            new FakeGlobalHotkeyService(),
            new VoiceSettings { IsEnabled = true, GlobalPushToTalk = true, PushToTalkKeyName = "F8" },
            new ScreenshotSettings { GlobalHotkeyEnabled = true, HotkeyKeyName = "F8" },
            logger);

        await coordinator.ApplyAsync();

        // What the sentence says is GlobalHotkeyConflictCheckTests'; that it reaches the operator is this one's.
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    /// <summary>Every press carries the id of the key that fired, so a feature can tell its own from another's.</summary>
    [Fact]
    public async Task APress_IsForwardedWithTheIdOfTheKeyThatFired()
    {
        var service = new FakeGlobalHotkeyService();
        var coordinator = TestGlobalHotkeys.Coordinator(service, screenshots: new ScreenshotSettings { GlobalHotkeyEnabled = true });
        var pressed = new List<string>();
        coordinator.Pressed += (_, id) => pressed.Add(id);
        await coordinator.ApplyAsync();

        service.RaisePressed(GlobalHotkeys.Screenshot);

        Assert.Equal(new[] { GlobalHotkeys.Screenshot }, pressed);
    }

    /// <summary>
    /// AC-71: neither hotkey backend can tell a hidden truth — that a compositor bound the key to a different,
    /// already-running cockpit, or that a keyboard hook installed while another instance's hook is doing the
    /// same. <see cref="IHotkeyExclusivityGuard"/> refusing the claim is the one signal that survives both
    /// backends reporting success. A key another instance already holds must not be armed, and must not read as
    /// "the operator never switched it on" — it is reported, once, rather than silently doing nothing.
    /// </summary>
    [Fact]
    public async Task AKeyAnotherInstanceAlreadyHolds_IsNotArmed_AndReportsTheConflictOnce()
    {
        var guard = Substitute.For<IHotkeyExclusivityGuard>();
        guard.TryAcquire(GlobalHotkeys.PushToTalk).Returns((IDisposable?)null);
        var toasts = Substitute.For<IToastService>();
        var service = new FakeGlobalHotkeyService();
        var coordinator = TestGlobalHotkeys.Coordinator(
            service, TestGlobalHotkeys.GlobalPushToTalkOn, guard: guard, toasts: toasts);

        await coordinator.ApplyAsync();
        await coordinator.ApplyAsync();

        coordinator.IsArmed(GlobalHotkeys.PushToTalk).Should().BeFalse("another cockpit instance already holds the key");
        service.LastBindings.Should().BeEmpty("the conflicted binding must never reach the OS service");
        toasts.Received(1).Show(Arg.Is<string>(message => message.Contains("another cockpit instance")), ToastSeverity.Warning);
    }

    /// <summary>A key nobody else is competing for is claimed and reaches the OS service exactly as before AC-71.</summary>
    [Fact]
    public async Task AKeyNobodyElseHolds_IsClaimedAndArmed()
    {
        var service = new FakeGlobalHotkeyService();
        var coordinator = TestGlobalHotkeys.Coordinator(service, TestGlobalHotkeys.GlobalPushToTalkOn);

        await coordinator.ApplyAsync();

        coordinator.IsArmed(GlobalHotkeys.PushToTalk).Should().BeTrue();
        service.LastBindings.Should().ContainSingle(binding => binding.Id == GlobalHotkeys.PushToTalk);
    }

    /// <summary>
    /// The holder disappearing (the other cockpit instance closing) must not need a restart to notice — the
    /// whole reason AC-71 exists: a conflict that resolves itself has to be picked back up on its own.
    /// </summary>
    [Fact]
    public async Task WhenTheOtherInstanceReleasesTheKey_ARetryArmsItWithoutBeingAskedAgain()
    {
        var guard = Substitute.For<IHotkeyExclusivityGuard>();
        guard.TryAcquire(GlobalHotkeys.PushToTalk).Returns(
            _ => (IDisposable?)null, _ => Substitute.For<IDisposable>());
        var service = new FakeGlobalHotkeyService();
        var coordinator = TestGlobalHotkeys.Coordinator(
            service,
            TestGlobalHotkeys.GlobalPushToTalkOn,
            guard: guard,
            retryInterval: TimeSpan.FromMilliseconds(20));

        await coordinator.ApplyAsync();
        coordinator.IsArmed(GlobalHotkeys.PushToTalk).Should().BeFalse("the first attempt found the key held");

        await _WaitUntilAsync(() => coordinator.IsArmed(GlobalHotkeys.PushToTalk));

        coordinator.IsArmed(GlobalHotkeys.PushToTalk).Should().BeTrue("the retry timer claimed it once it came free");
    }

    /// <summary>A retry that is still conflicted must not nag the operator again with the same news.</summary>
    [Fact]
    public async Task AConflictThatHasNotResolvedYet_DoesNotToastTwice()
    {
        var guard = Substitute.For<IHotkeyExclusivityGuard>();
        guard.TryAcquire(GlobalHotkeys.PushToTalk).Returns((IDisposable?)null);
        var toasts = Substitute.For<IToastService>();
        var coordinator = TestGlobalHotkeys.Coordinator(
            new FakeGlobalHotkeyService(),
            TestGlobalHotkeys.GlobalPushToTalkOn,
            guard: guard,
            toasts: toasts,
            retryInterval: TimeSpan.FromMilliseconds(20));

        await coordinator.ApplyAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        toasts.Received(1).Show(Arg.Any<string>(), ToastSeverity.Warning);
    }

    /// <summary>Switching the feature off must release the claim rather than holding a key nobody wants any more.</summary>
    [Fact]
    public async Task SwitchingTheFeatureOff_ReleasesItsClaim()
    {
        var claim = Substitute.For<IDisposable>();
        var guard = Substitute.For<IHotkeyExclusivityGuard>();
        guard.TryAcquire(GlobalHotkeys.PushToTalk).Returns(claim);
        var voiceStore = Substitute.For<IVoiceSettingsStore>();
        voiceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(
            TestGlobalHotkeys.GlobalPushToTalkOn, new VoiceSettings { IsEnabled = true, GlobalPushToTalk = false });
        var screenshotStore = Substitute.For<IScreenshotSettingsStore>();
        screenshotStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new ScreenshotSettings());
        var coordinator = new GlobalHotkeyCoordinator(
            new FakeGlobalHotkeyService(),
            voiceStore,
            screenshotStore,
            guard,
            Substitute.For<IToastService>(),
            NullLogger<GlobalHotkeyCoordinator>.Instance);

        await coordinator.ApplyAsync();
        await coordinator.ApplyAsync();

        claim.Received(1).Dispose();
    }

    private static async Task _WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }
}
