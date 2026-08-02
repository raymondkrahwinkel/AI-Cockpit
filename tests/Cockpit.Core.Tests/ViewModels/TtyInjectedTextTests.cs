using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// A TTY panel has no input box: injected text is written into the pty as raw bytes, the same path a keystroke takes
/// (the view's <c>VoiceTranscriptReady</c> handler is that write). So the pty must be handed text and nothing but
/// text — a carriage return in it is the Enter nobody pressed, and an escape sequence drives the TUI rather than
/// filling its composer. These cover that <see cref="SessionPanelViewModel.InjectText"/> keeps its promise ("places,
/// does not send") whatever the text contains, while the paths that mean to submit —
/// <see cref="SessionPanelViewModel.InjectAndSubmit"/> and the voice auto-submit — still send their own carriage
/// return.
/// </summary>
public class TtyInjectedTextTests
{
    // Written as codes rather than escapes so the bytes under test are named, and so no invisible character ends up
    // in this file: Ctrl+C, completion, backspace, and the two introducers every ANSI sequence is built from.
    private const char Interrupt = (char)0x03;
    private const char Tab = (char)0x09;
    private const char Backspace = (char)0x7f;
    private const char Escape = (char)0x1b;
    private const char Bell = (char)0x07;

    [Fact]
    public void InjectText_WithLineBreaks_WritesNoCarriageReturnIntoThePty()
    {
        var writes = new List<string>();
        var vm = _NewTty(writes);

        vm.InjectText("Fix the login bug.\r\nSteps:\rone\ntwo");

        Assert.Equal(new[] { "Fix the login bug. Steps: one two" }, writes);
    }

    [Fact]
    public void InjectText_WithControlBytes_TurnsThemIntoSpacesInsteadOfKeypresses()
    {
        var writes = new List<string>();
        var vm = _NewTty(writes);

        vm.InjectText($"before{Interrupt}after{Tab}tab{Backspace}");

        Assert.Equal(new[] { "before after tab" }, writes);
    }

    [Fact]
    public void InjectText_WithAnsiEscapeSequences_KeepsThemOutOfThePty()
    {
        var writes = new List<string>();
        var vm = _NewTty(writes);

        // A colour run (CSI), a window-title write (OSC) and a bracketed-paste marker — none of it is text, and each
        // is dropped whole, payload included: an escape sequence separates nothing, so nothing takes its place.
        vm.InjectText($"{Escape}[31mred{Escape}[0m and {Escape}]0;pwned{Bell}plain{Escape}[200~");

        Assert.Equal(new[] { "red and plain" }, writes);
    }

    [Fact]
    public void InjectText_ThatIsNothingButKeys_WritesNothingAtAll()
    {
        var writes = new List<string>();
        var vm = _NewTty(writes);

        vm.InjectText("\r\n");

        Assert.Empty(writes);
    }

    [Fact]
    public void InjectAndSubmit_StillSubmits_WhereInjectTextOfTheSameTextDoesNot()
    {
        var writes = new List<string>();
        var vm = _NewTty(writes);

        // AC-64 schedules the submit a beat after the text; run it inline so the ordering is assertable.
        vm.SetAutoSubmitScheduler(submit => submit());

        vm.InjectAndSubmit("run the tests\r");

        // The text, then a carriage return of its own. Which of the two seams was called now decides whether the
        // session is submitted to, instead of whether the caller's text happened to carry a line break.
        Assert.Equal(new[] { "run the tests", "\r" }, writes);
    }

    [Fact]
    public async Task SendPromptAsync_TypesTheSanitisedPrompt_ThenExactlyOneCarriageReturn()
    {
        var vm = _NewTty([]);
        var written = new List<string>();
        vm.PromptSink = text => written.Add(text);

        Assert.True((await vm.SendPromptAsync($"resume {Escape}[2J the\r\nreview")));

        Assert.Equal(new[] { "resume the review\r" }, written);
    }

    /// <summary>
    /// The spawn bug, at the seam it happened on. A TTY pane's view subscribes to <c>VoiceTranscriptReady</c> when the
    /// data context attaches, but only wires <see cref="TtyViewModel.PromptSink"/> once the pty process has actually
    /// spawned — so between those two moments there is a listener and still nowhere for text to go. A brief handed
    /// over then must wait for the pty, not be published into the gap.
    /// </summary>
    [Fact]
    public void SubmitPromptWhenReady_BeforeThePtyIsUp_HoldsTheBrief_ThenSendsItWhenTheSinkArrives()
    {
        var writes = new List<string>();
        var vm = _NewTty(writes);
        vm.SetAutoSubmitScheduler(submit => submit());

        var wentOutNow = vm.SubmitPromptWhenReady("start on the migration");

        Assert.False(wentOutNow);
        Assert.Empty(writes);
        Assert.True(vm.HasPromptWaitingToBeDelivered);

        // What TtyView.StartPty does the instant the pty exists — and the measured moment text written into that pty
        // reaches the child process.
        vm.PromptSink = _ => { };

        Assert.Equal(new[] { "start on the migration", "\r" }, writes);
        Assert.False(vm.HasPromptWaitingToBeDelivered);
    }

    /// <summary>A pane whose pty is already up takes the brief straight away — the wait is for the condition, not for a delay.</summary>
    [Fact]
    public void SubmitPromptWhenReady_OnAPaneWhosePtyIsAlreadyUp_SendsImmediately()
    {
        var writes = new List<string>();
        var vm = _NewTty(writes);
        vm.SetAutoSubmitScheduler(submit => submit());
        vm.PromptSink = _ => { };

        Assert.True(vm.SubmitPromptWhenReady("start on the migration"));
        Assert.Equal(new[] { "start on the migration", "\r" }, writes);
    }

    /// <summary>A TTY panel that records into <paramref name="writes"/> every write it asks the view to make into the pty, in order.</summary>
    private static TtyViewModel _NewTty(List<string> writes)
    {
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(Arg.Any<SessionProfile?>()).Returns(Substitute.For<ITtySessionProvider>());

        var vm = new TtyViewModel(Substitute.For<ITtyLauncher>(), resolver);
        vm.VoiceTranscriptReady += writes.Add;

        return vm;
    }
}
