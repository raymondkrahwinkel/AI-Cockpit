using Cockpit.Core.Voice;

namespace Cockpit.App.ViewModels;

// A selectable Whisper backend preference: display label plus the `VoiceBackendPreference` value, and (AC-1881)
// the Vulkan UUID of the GPU when the host has several to choose from; empty means Auto picks the GPU.
public sealed record VoiceBackendPreferenceOption(string Label, VoiceBackendPreference Value, string GpuDeviceId = "");
