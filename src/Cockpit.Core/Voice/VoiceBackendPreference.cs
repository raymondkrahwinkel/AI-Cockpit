namespace Cockpit.Core.Voice;

/// <summary>
/// User-facing Whisper backend preference (the "voice" section's <c>cockpit.json</c> setting).
/// <see cref="Auto"/> lets <see cref="WhisperBackendPlanner"/> pick the best order for the current
/// OS; the others pin a specific runtime family, still with a CPU tail so transcription never hard-fails.
/// </summary>
public enum VoiceBackendPreference
{
    Auto,
    Cuda,
    Vulkan,
    Cpu,
}
