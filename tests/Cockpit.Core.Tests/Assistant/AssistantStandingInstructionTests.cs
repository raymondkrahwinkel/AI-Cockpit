using Cockpit.Core.Assistant;

namespace Cockpit.Core.Tests.Assistant;

/// <summary>
/// AC-759: the acting paragraph's two gates — the SDK's own Allow/Deny for starting or stopping a session, and the
/// cockpit consent card for reaching into one or moving the assistant's memory — are composed independently, so a
/// session on a profile that skips one still hears the truth about the other rather than one folded "asking is on"
/// flag.
/// </summary>
public sealed class AssistantStandingInstructionTests
{
    // Distinctive fragments of each of the four halves `AssistantSystemPrompt.ActingParagraph` composes from,
    // read as substrings rather than by referencing the (deliberately private) constants themselves — the same
    // arm's-length check the rest of this file's assertions use.
    private const string GateAAsksFragment = "spelling out the profile, the desk and the folder";
    private const string GateABypassedFragment = "set to bypass permissions, so the call simply goes ahead";
    private const string GateBAsksFragment = "can raise a card of its own too";
    private const string GateBBypassedFragment = "the operator switched that asking off";

    [Fact]
    public void BothGatesAsking_IsExactlyAssistantSystemPromptDefault()
    {
        var instruction = AssistantStandingInstruction.Compose(
            null, replacesDefault: false, memory: null, currentState: null, sdkAsksPermission: true, consentCardAsks: true);

        Assert.Equal(AssistantSystemPrompt.Default, instruction);
    }

    /// <summary>
    /// Criterion 3: a profile on <c>bypassPermissions</c> gets no "a permission is waiting" instruction for
    /// starting or stopping — the exact sentence AC-768 already stripped everywhere, checked here to stay gone
    /// under every gate combination rather than only the default one.
    /// </summary>
    [Fact]
    public void SdkGateBypassed_CarriesNoSayItIsWaitingSentence()
    {
        var instruction = AssistantStandingInstruction.Compose(
            null, replacesDefault: false, memory: null, currentState: null, sdkAsksPermission: false, consentCardAsks: true);

        Assert.DoesNotContain("I need your permission, have a look at your screen", instruction, StringComparison.Ordinal);
        Assert.Contains(GateABypassedFragment, instruction, StringComparison.Ordinal);
        Assert.DoesNotContain(GateAAsksFragment, instruction, StringComparison.Ordinal);
    }

    /// <summary>
    /// Criterion 4: the common install — an ordinary profile, plus the default "bypass everything" consent switch
    /// (<c>AssistantSettings.ConsentBypassAll</c>) — names the two gates apart rather than folding them into one
    /// verdict: starting or stopping still costs a click, sending or moving memory does not.
    /// </summary>
    [Fact]
    public void SdkGateAsks_ConsentGateBypassed_NamesBothGatesApart()
    {
        var instruction = AssistantStandingInstruction.Compose(
            null, replacesDefault: false, memory: null, currentState: null, sdkAsksPermission: true, consentCardAsks: false);

        Assert.Contains(GateAAsksFragment, instruction, StringComparison.Ordinal);
        Assert.Contains(GateBBypassedFragment, instruction, StringComparison.Ordinal);
        Assert.DoesNotContain(GateBAsksFragment, instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void BothGatesBypassed_CarriesNeitherAsksFragment()
    {
        var instruction = AssistantStandingInstruction.Compose(
            null, replacesDefault: false, memory: null, currentState: null, sdkAsksPermission: false, consentCardAsks: false);

        Assert.Contains(GateABypassedFragment, instruction, StringComparison.Ordinal);
        Assert.Contains(GateBBypassedFragment, instruction, StringComparison.Ordinal);
        Assert.DoesNotContain(GateAAsksFragment, instruction, StringComparison.Ordinal);
        Assert.DoesNotContain(GateBAsksFragment, instruction, StringComparison.Ordinal);
    }

    /// <summary>The operator's own replacement text is unaffected by either gate — it replaces the whole default.</summary>
    [Fact]
    public void ReplacingTheDefault_IgnoresBothGates()
    {
        var instruction = AssistantStandingInstruction.Compose(
            "You are a terse assistant.", replacesDefault: true, memory: null, currentState: null,
            sdkAsksPermission: false, consentCardAsks: false);

        Assert.Equal("You are a terse assistant.", instruction);
    }
}
