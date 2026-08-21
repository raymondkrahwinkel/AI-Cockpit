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
        vm.SetAutoSubmitScheduler((_, submit) => submit());

        vm.InjectAndSubmit("run the tests\r");

        // The text, then a carriage return of its own. Which of the two seams was called now decides whether the
        // session is submitted to, instead of whether the caller's text happened to carry a line break.
        Assert.Equal(new[] { "run the tests", "\r" }, writes);
    }

    /// <summary>
    /// AC-993: the beat before the submitting CR has to outlast the CLI's handling of the paste it follows. A spoken
    /// transcript is a few words and AC-64's 60ms covered it; an agent brief is kilobytes, and a CR that lands while
    /// the CLI is still taking that paste in is swallowed — the brief stays in the input as an unsent
    /// <c>[Pasted Text #N]</c>. So the gap scales with what was just typed, while a short transcript keeps its 60ms.
    /// </summary>
    [Fact]
    public void InjectAndSubmit_OfAMultiKilobyteBrief_WaitsLongerBeforeTheCarriageReturnThanAShortTranscriptDoes()
    {
        var vm = _NewTty([]);
        var delays = new List<TimeSpan>();
        vm.SetAutoSubmitScheduler((delay, submit) => { delays.Add(delay); submit(); });

        vm.InjectAndSubmit("send it");
        vm.InjectAndSubmit(new string('b', 2048)); // an agent brief, well over AC-752's 64-byte paste threshold

        Assert.True(delays[0] < TimeSpan.FromMilliseconds(70), $"a short transcript got {delays[0].TotalMilliseconds}ms, not AC-64's beat");
        Assert.True(delays[1] > TimeSpan.FromMilliseconds(500), $"a 2 KB brief got only {delays[1].TotalMilliseconds}ms");
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
    /// <para>
    /// AC-760: the pty existing is necessary but no longer sufficient — <see cref="TtyViewModel.CanTakeAPrompt"/> also
    /// needs <see cref="TtyViewModel.MarkHostedTuiReady"/>, the signal that the hosted CLI is actually reading stdin
    /// (not just that its process was spawned). This pins both halves of that gate separately.
    /// </para>
    /// </summary>
    [Fact]
    public void SubmitPromptWhenReady_BeforeThePtyIsUp_HoldsTheBrief_ThenSendsItWhenTheSinkArrives()
    {
        var writes = new List<string>();
        var vm = _NewTty(writes);
        vm.SetAutoSubmitScheduler((_, submit) => submit());

        var wentOutNow = vm.SubmitPromptWhenReady("start on the migration");

        Assert.False(wentOutNow);
        Assert.Empty(writes);
        Assert.True(vm.HasPromptWaitingToBeDelivered);

        // What TtyView.StartPty does the instant the pty exists — necessary, not sufficient (AC-760): the process
        // existing is not yet the CLI reading stdin, so the brief stays held.
        vm.PromptSink = _ => { };

        Assert.Empty(writes);
        Assert.True(vm.HasPromptWaitingToBeDelivered);

        // What TtyView's readiness gate calls once the hosted CLI has announced itself (DECSET 2004) or the
        // fallback deadline elapsed — the moment text written into the pty now actually reaches the child process.
        vm.MarkHostedTuiReady();

        Assert.Equal(new[] { "start on the migration", "\r" }, writes);
        Assert.False(vm.HasPromptWaitingToBeDelivered);
    }

    /// <summary>A pane whose pty is already up and whose hosted CLI is already ready takes the brief straight away — the wait is for the condition, not for a delay.</summary>
    [Fact]
    public void SubmitPromptWhenReady_OnAPaneThatIsAlreadyReady_SendsImmediately()
    {
        var writes = new List<string>();
        var vm = _NewTty(writes);
        vm.SetAutoSubmitScheduler((_, submit) => submit());
        vm.PromptSink = _ => { };
        vm.MarkHostedTuiReady();

        Assert.True(vm.SubmitPromptWhenReady("start on the migration"));
        Assert.Equal(new[] { "start on the migration", "\r" }, writes);
    }

    /// <summary>AC-760: the pty being up is not readiness — a sink with no readiness signal still refuses to send.</summary>
    [Fact]
    public void SubmitPromptWhenReady_OnAPaneWhosePtyIsUpButNotYetReady_StillHoldsTheBrief()
    {
        var writes = new List<string>();
        var vm = _NewTty(writes);
        vm.PromptSink = _ => { };

        Assert.False(vm.SubmitPromptWhenReady("start on the migration"));
        Assert.Empty(writes);
        Assert.True(vm.HasPromptWaitingToBeDelivered);
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
