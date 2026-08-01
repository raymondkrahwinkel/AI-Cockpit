using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Assistant;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Screenshots;
using Cockpit.Core.Voice;

namespace Cockpit.Core.Tests.Hotkeys;

/// <summary>
/// Builds a <see cref="GlobalHotkeyCoordinator"/> over a fake OS service and stubbed settings stores — the
/// arrangement every test of a hotkey-driven feature needs, since the coordinator is now what a feature
/// subscribes to rather than the service itself.
/// </summary>
internal static class TestGlobalHotkeys
{
    public static GlobalHotkeyCoordinator Coordinator(
        FakeGlobalHotkeyService hotkeys,
        VoiceSettings? voice = null,
        ScreenshotSettings? screenshots = null,
        ILogger<GlobalHotkeyCoordinator>? logger = null,
        IHotkeyExclusivityGuard? guard = null,
        IToastService? toasts = null,
        TimeSpan? retryInterval = null,
        AssistantSettings? assistant = null)
    {
        var voiceStore = Substitute.For<IVoiceSettingsStore>();
        voiceStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(voice ?? new VoiceSettings());

        var screenshotStore = Substitute.For<IScreenshotSettingsStore>();
        screenshotStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(screenshots ?? new ScreenshotSettings());

        // Defaults to the assistant switched off, which is what a fresh install has — so an existing test that
        // says nothing about the assistant keeps asserting over exactly the bindings it always did.
        var assistantStore = Substitute.For<IAssistantSettingsStore>();
        assistantStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(assistant ?? new AssistantSettings());

        return new GlobalHotkeyCoordinator(
            hotkeys,
            voiceStore,
            screenshotStore,
            assistantStore,
            guard ?? AlwaysAvailable(),
            toasts ?? Substitute.For<IToastService>(),
            logger ?? NullLogger<GlobalHotkeyCoordinator>.Instance,
            retryInterval);
    }

    /// <summary>A guard that grants every claim — the ordinary case, where no other cockpit instance is competing for a key.</summary>
    public static IHotkeyExclusivityGuard AlwaysAvailable()
    {
        var guard = Substitute.For<IHotkeyExclusivityGuard>();
        guard.TryAcquire(Arg.Any<string>()).Returns(_ => Substitute.For<IDisposable>());
        return guard;
    }

    /// <summary>Voice settings with the desktop-wide hold switched on — the state in which push-to-talk contributes a binding.</summary>
    public static VoiceSettings GlobalPushToTalkOn => new() { IsEnabled = true, GlobalPushToTalk = true };
}
