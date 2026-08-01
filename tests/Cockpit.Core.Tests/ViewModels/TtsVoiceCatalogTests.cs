using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The voice picker's list (AC-546). The catalogue's doc-comment promises that a label can never disagree with the
/// number it selects — the failure it was written to end, where "Voice 2" meant sid 3. These pin that promise.
/// </summary>
public class TtsVoiceCatalogTests
{
    [Fact]
    public void EveryVoicesSidIsItsOwnPositionInTheList()
    {
        // The whole point of generating the list rather than hand-writing it: the synthesizer is handed the sid, so
        // a list whose order and sids disagree hands the operator a different voice than the one they picked.
        Assert.All(
            TtsVoiceCatalog.Voices.Select((voice, index) => (voice, index)),
            pair => Assert.Equal(pair.index, pair.voice.Sid));
    }

    [Fact]
    public void TheListHoldsTheTenSpeakersTheModelWasMeasuredToHave()
    {
        // Measured 2026-08-02 with OfflineTts.NumSpeakers against sherpa-onnx-supertonic-3-tts-int8-2026-05-11.
        // Asserted against the exact number, not "more than a few": a list that silently lost a voice would pass a
        // loose bound while a sid the model rejects would reach the synthesizer.
        Assert.Equal(10, TtsVoiceCatalog.Voices.Count);
    }

    [Fact]
    public void EveryVoiceIsLabelledAndNoTwoLabelsAreTheSame()
    {
        // A picker with two identically-named rows cannot be used to choose between them.
        Assert.All(TtsVoiceCatalog.Voices, voice => Assert.False(string.IsNullOrWhiteSpace(voice.Label)));
        Assert.Equal(
            TtsVoiceCatalog.Voices.Count,
            TtsVoiceCatalog.Voices.Select(voice => voice.Label).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheFemaleStylesComeBeforeTheMaleOnes_TheOrderTheSidsAreBuiltFrom()
    {
        // sherpa-onnx builds voice.bin from sorted(*.json) over F1..F5, M1..M5, so the sid order is not a free
        // choice — it follows that sort. Pinning it here means a re-ordering of this list has to argue with the
        // build recipe rather than quietly relabel someone's saved voice.
        var labels = TtsVoiceCatalog.Voices.Select(voice => voice.Label).ToList();

        Assert.All(labels.Take(5), label => Assert.StartsWith("F", label, StringComparison.Ordinal));
        Assert.All(labels.Skip(5), label => Assert.StartsWith("M", label, StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultIsStillSidOne_SoAnExistingChoiceDoesNotMoveWhenTheListGrows()
    {
        Assert.Equal(1, TtsVoiceCatalog.Default.Sid);
        Assert.Contains(TtsVoiceCatalog.Default, TtsVoiceCatalog.Voices);
    }
}
