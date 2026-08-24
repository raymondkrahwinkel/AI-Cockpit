namespace Cockpit.Infrastructure.Voice;

// On-disk paths of the downloaded-and-extracted SupertonicTTS model, resolved by SupertonicModelCache.
// Supertonic ships a fixed file set (four int8 ONNX graphs plus tokenizer json, unicode indexer,
// packed voice-style embeddings) rather than the single model+tokens pair a Piper voice used.
internal sealed record SupertonicModelPaths(
    string DurationPredictorPath,
    string TextEncoderPath,
    string VectorEstimatorPath,
    string VocoderPath,
    string TtsJsonPath,
    string UnicodeIndexerPath,
    string VoiceStylePath);
