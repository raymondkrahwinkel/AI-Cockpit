using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>Records every call and returns a small fixed waveform — stands in for the real sherpa-onnx engine in queue/wiring tests.</summary>
internal sealed class FakeTextToSpeechService : ITextToSpeechService
{
    public List<(string Text, int SpeakerId, string Language)> Calls { get; } = [];

    public Task<TtsAudio> SynthesizeAsync(string text, int speakerId, string language, CancellationToken cancellationToken = default)
    {
        Calls.Add((text, speakerId, language));
        return Task.FromResult(new TtsAudio(Samples: [0.1f, -0.1f], SampleRate: 22050));
    }
}
