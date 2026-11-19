using Cockpit.Core.Rendering;

namespace Cockpit.Infrastructure.Rendering;

public static class ChromiumGpuArguments
{
    public const string EnvironmentVariable = "COCKPIT_CHROMIUM_GPU_ARGS";

    public static string[] For(GpuPreference preference) => OperatingSystem.IsWindows()
        ? [
            "--enable-gpu",
            "--use-gl=angle",
            "--use-angle=d3d11",
            "--disable-software-rasterizer",
            preference is GpuPreference.Integrated ? "--force_low_power_gpu" : "--force_high_performance_gpu",
        ]
        : [];
}
