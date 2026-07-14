using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Voice-transcript injection routes differently per session kind: the SDK panel appends the
/// transcript to its input box for the operator to proofread before sending, while the TTY panel has
/// no input box and instead raises an event the view writes as raw bytes into the pty — this is the
/// "TTY-bytes vs SDK-text" split from the voice-input design. Also covers the shared
/// <see cref="SessionPanelViewModel"/> hold-guard/gating plumbing (voice-off gate, cleanup toggle) both
/// session kinds inherit.
/// </summary>
public class VoiceInjectionTests
{
    [Fact]
    public async Task SdkSession_VoiceTranscript_IsAppendedToTheInputBox_WithCleanupApplied()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(true);
        voicePushToTalk.EndHoldAsync(applyCleanup: true, Arg.Any<CancellationToken>()).Returns("Open the settings dialog.");
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true, PushToTalkKeyName = "F9" });

        var vm = new SessionViewModel(new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePushToTalk, voiceSettingsStore)
        {
            InputText = "before ",
        };
        await _WaitForVoiceSettingsToLoadAsync(() => vm.VoiceEnabled);

        vm.BeginVoiceHold().Should().BeTrue();
        await vm.EndVoiceHoldAsync(applyCleanup: true);

        vm.InputText.Should().Be("before  Open the settings dialog.");
        await voicePushToTalk.Received(1).EndHoldAsync(applyCleanup: true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TtySession_VoiceTranscript_RaisesRawEvent_WithoutCleanup_InsteadOfTouchingAnInputBox()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(true);
        voicePushToTalk.EndHoldAsync(applyCleanup: false, Arg.Any<CancellationToken>()).Returns("open the settings dialog");
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true, PushToTalkKeyName = "F9" });

        var vm = new ClaudeTtyViewModel(Substitute.For<ITtyLauncher>(), Substitute.For<ITtySessionProvider>(), voicePushToTalk, voiceSettingsStore);
        await _WaitForVoiceSettingsToLoadAsync(() => vm.VoiceEnabled);

        string? rawTranscript = null;
        vm.VoiceTranscriptReady += text => rawTranscript = text;

        vm.BeginVoiceHold().Should().BeTrue();
        await vm.EndVoiceHoldAsync(applyCleanup: false);

        rawTranscript.Should().Be("open the settings dialog");
        await voicePushToTalk.Received(1).EndHoldAsync(applyCleanup: false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TtySession_WhenAutoSubmitOn_WritesACarriageReturnAfterTheTranscript()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(true);
        voicePushToTalk.EndHoldAsync(applyCleanup: false, Arg.Any<CancellationToken>()).Returns("open the settings dialog");
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(
            new VoiceSettings { IsEnabled = true, PushToTalkKeyName = "F9", AutoSubmitAfterVoice = true });

        var vm = new ClaudeTtyViewModel(Substitute.For<ITtyLauncher>(), Substitute.For<ITtySessionProvider>(), voicePushToTalk, voiceSettingsStore);
        await _WaitForVoiceSettingsToLoadAsync(() => vm.AutoSubmitAfterVoice);

        var writes = new List<string>();
        vm.VoiceTranscriptReady += text => writes.Add(text);

        vm.BeginVoiceHold().Should().BeTrue();
        await vm.EndVoiceHoldAsync(applyCleanup: false);

        // The transcript first, then a lone carriage return — the byte a physical Enter sends into the pty.
        writes.Should().Equal("open the settings dialog", "\r");
    }

    [Fact]
    public async Task BeginVoiceHold_WhenVoiceDisabled_NeverCallsTheService()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = false });

        var vm = new SessionViewModel(new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePushToTalk, voiceSettingsStore);
        await _WaitForVoiceSettingsToLoadAsync(() => !vm.VoiceEnabled);

        vm.BeginVoiceHold().Should().BeFalse();
        voicePushToTalk.DidNotReceiveWithAnyArgs().BeginHold();
    }

    /// <summary>Voice settings load asynchronously in the constructor (fire-and-forget); polls briefly rather than assuming synchronous completion.</summary>
    private static async Task _WaitForVoiceSettingsToLoadAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }
}
