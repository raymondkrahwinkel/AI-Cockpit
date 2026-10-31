using System.Diagnostics;
using System.Globalization;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;

namespace Cockpit.Infrastructure.Voice;

// AC-1881: Points a transcription child at one GPU. whisper.cpp otherwise takes device 0, which on an Optimus
// laptop is the iGPU. Set per child process, so nothing in Whisper.net changes; with one GPU nothing is set.
internal static class GpuDeviceEnvironment
{
    public const string VulkanDevicesVariable = "GGML_VK_VISIBLE_DEVICES";
    public const string CudaDevicesVariable = "CUDA_VISIBLE_DEVICES";

    // Returns the GPU the child was pointed at, or null when it was left to whisper.cpp's default.
    public static GpuHardware? Apply(ProcessStartInfo startInfo, VoiceBackendPreference backend, IReadOnlyList<GpuHardware> gpus, string? deviceId)
    {
        if (backend is VoiceBackendPreference.Cpu || gpus.Count < 2)
        {
            return null;
        }

        var gpu = GpuHardware.Resolve(gpus, deviceId);
        if (gpu.VulkanIndex is { } index)
        {
            startInfo.Environment[VulkanDevicesVariable] = index.ToString(CultureInfo.InvariantCulture);
        }

        // The Vulkan UUID is the CUDA UUID; selecting by it sidesteps CUDA's own fastest-first device order.
        if (gpu.Vendor is GpuVendor.Nvidia && gpu.DeviceId is { } uuid)
        {
            startInfo.Environment[CudaDevicesVariable] = "GPU-" + uuid;
        }

        return gpu;
    }
}
