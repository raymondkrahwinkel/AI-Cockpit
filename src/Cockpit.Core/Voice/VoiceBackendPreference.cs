namespace Cockpit.Core.Voice;

// User-facing Whisper backend preference (the "voice" section's `cockpit.json` setting).
// `Auto` lets `WhisperBackendPlanner` pick the best order for the current
// OS; the others pin a specific runtime family, still with a CPU tail so transcription never hard-fails.
public enum VoiceBackendPreference
{
    Auto,
    Cuda,
    Vulkan,
    Cpu,
}
