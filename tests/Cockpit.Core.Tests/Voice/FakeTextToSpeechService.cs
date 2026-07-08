using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>Records every call and returns a small fixed waveform — stands in for the real sherpa-onnx engine in queue/wiring tests.</summary>
internal sealed class FakeTextToSpeechService : ITextToSpeechService
{
    public List<(string Text, string VoiceId)> Calls { get; } = [];

    public Task<TtsAudio> SynthesizeAsync(string text, string voiceId, CancellationToken cancellationToken = default)
    {
        Calls.Add((text, voiceId));
        return Task.FromResult(new TtsAudio(Samples: [0.1f, -0.1f], SampleRate: 22050));
    }
}
