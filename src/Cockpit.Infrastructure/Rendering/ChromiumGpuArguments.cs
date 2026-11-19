using Cockpit.Shared;

namespace Cockpit.Infrastructure.Rendering;

public static class ChromiumGpuArguments
{
    public const string EnvironmentVariable = "COCKPIT_CHROMIUM_GPU_ARGS";
    public const string DeviceNameVariable = DxgiAdapters.DeviceNameVariable;

    public static string[] For(string? deviceName) => DxgiAdapters.ChromiumArguments(deviceName);
}
