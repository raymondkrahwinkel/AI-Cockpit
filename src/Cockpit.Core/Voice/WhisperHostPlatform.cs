namespace Cockpit.Core.Voice;

/// <summary>
/// The operating systems the cockpit resolves Whisper runtimes for. A boolean cannot say "macOS": the flag
/// this replaced meant "not Windows" and every caller read that as Linux, which is how a Mac ended up being
/// offered CUDA and promised a NoAvx runtime that is not published for it.
/// </summary>
public enum WhisperHostPlatform
{
    Windows,
    Linux,
    MacOs,
}
