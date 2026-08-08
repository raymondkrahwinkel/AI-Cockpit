using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Voice-transcript injection routes differently per session kind: the SDK panel appends the
/// transcript to its input box for the operator to proofread before sending, while the TTY panel has
/// no input box and instead raises an event the view writes as raw bytes into the pty — this is the
/// "TTY-bytes vs SDK-text" split from the voice-input design. Also covers the shared
/// <see cref="SessionPanelViewModel"/> hold-guard/gating plumbing (voice-off gate) both session kinds
/// inherit.
/// </summary>
public class VoiceInjectionTests
{
    [Fact]
    public async Task SdkSession_VoiceTranscript_IsAppendedToTheInputBox()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(true);
        voicePushToTalk.EndHoldAsync(Arg.Any<CancellationToken>()).Returns("Open the settings dialog.");
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true, PushToTalkKeyName = "F9" });

        var vm = new SessionViewModel(new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePushToTalk, voiceSettingsStore)
        {
            InputText = "before ",
        };
        await _WaitForVoiceSettingsToLoadAsync(() => vm.VoiceEnabled);

        Assert.True(vm.BeginVoiceHold());
        await vm.EndVoiceHoldAsync();

        Assert.Equal("before  Open the settings dialog.", vm.InputText);
        await voicePushToTalk.Received(1).EndHoldAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TtySession_VoiceTranscript_RaisesRawEvent_InsteadOfTouchingAnInputBox()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(true);
        voicePushToTalk.EndHoldAsync(Arg.Any<CancellationToken>()).Returns("open the settings dialog");
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true, PushToTalkKeyName = "F9" });

        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver(), voicePushToTalk, voiceSettingsStore);
        await _WaitForVoiceSettingsToLoadAsync(() => vm.VoiceEnabled);

        string? rawTranscript = null;
        vm.VoiceTranscriptReady += text => rawTranscript = text;

        Assert.True(vm.BeginVoiceHold());
        await vm.EndVoiceHoldAsync();

        Assert.Equal("open the settings dialog", rawTranscript);
        await voicePushToTalk.Received(1).EndHoldAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TtySession_WhenAutoSubmitOn_WritesACarriageReturnAfterTheTranscript()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(true);
        voicePushToTalk.EndHoldAsync(Arg.Any<CancellationToken>()).Returns("open the settings dialog");
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(
            new VoiceSettings { IsEnabled = true, PushToTalkKeyName = "F9", AutoSubmitAfterVoice = true });

        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver(), voicePushToTalk, voiceSettingsStore);
        await _WaitForVoiceSettingsToLoadAsync(() => vm.AutoSubmitAfterVoice);

        var writes = new List<string>();
        vm.VoiceTranscriptReady += text => writes.Add(text);

        // AC-64: the auto-submit CR is scheduled as its own write a beat after the transcript (so ConPTY does not
        // coalesce them into one read on Windows). Run that schedule inline here so the ordering is assertable
        // without a real timer — the point under test is that the CR is a separate write that follows the text.
        vm.SetAutoSubmitScheduler(submit => submit());

        Assert.True(vm.BeginVoiceHold());
        await vm.EndVoiceHoldAsync();

        // The transcript first, then a lone carriage return — the byte a physical Enter sends into the pty.
        Assert.Equal(new[] { "open the settings dialog", "\r" }, writes);
    }

    [Fact]
    public async Task BeginVoiceHold_WhenVoiceDisabled_NeverCallsTheService()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = false });

        var vm = new SessionViewModel(new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePushToTalk, voiceSettingsStore);
        await _WaitForVoiceSettingsToLoadAsync(() => !vm.VoiceEnabled);

        Assert.False(vm.BeginVoiceHold());
        voicePushToTalk.DidNotReceiveWithAnyArgs().BeginHold();
    }

    /// <summary>
    /// AC-627 criterion 6: the in-window F9 handler and the desktop-wide one both come through this method, so
    /// open-mic steps aside once for both. In either coordinator instead, one of the two doors stays open.
    /// </summary>
    [Fact]
    public async Task AHoldWhileOpenMicIsListening_TakesTheMicrophoneOffIt_AndGivesItBackWhenTheHoldEnds()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(true);
        voicePushToTalk.EndHoldAsync(Arg.Any<CancellationToken>()).Returns("open the deployment notes");
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true });
        var openMic = Substitute.For<IOpenMicState>();
        openMic.IsListening.Returns(true);
        var suspension = Substitute.For<IDisposable>();
        openMic.SuspendForHold().Returns(suspension);

        var vm = new SessionViewModel(
            new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePushToTalk, voiceSettingsStore,
            openMicState: openMic);
        await _WaitForVoiceSettingsToLoadAsync(() => vm.VoiceEnabled);

        Assert.True(vm.BeginVoiceHold());
        openMic.Received(1).SuspendForHold();
        suspension.DidNotReceive().Dispose();

        await vm.EndVoiceHoldAsync();

        suspension.Received(1).Dispose();

        // And the words are where F9 promises they will be: in the composer, unsent, for the operator to read.
        Assert.Equal("open the deployment notes", vm.InputText);
    }

    /// <summary>A hold that never started takes nothing off open-mic — and so has nothing to give back.</summary>
    [Fact]
    public async Task AHoldTheServiceDeclines_LeavesOpenMicListening()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(false);
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true });
        var openMic = Substitute.For<IOpenMicState>();
        openMic.IsListening.Returns(true);

        var vm = new SessionViewModel(
            new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePushToTalk, voiceSettingsStore,
            openMicState: openMic);
        await _WaitForVoiceSettingsToLoadAsync(() => vm.VoiceEnabled);

        Assert.False(vm.BeginVoiceHold());

        openMic.DidNotReceive().SuspendForHold();
    }

    /// <summary>Resolves any profile (including none) to a fresh provider substitute — same as the real resolver does for a Claude profile or a profile-less session.</summary>
    private static ITtySessionProviderResolver _Resolver()
    {
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(Arg.Any<SessionProfile?>()).Returns(Substitute.For<ITtySessionProvider>());
        return resolver;
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
