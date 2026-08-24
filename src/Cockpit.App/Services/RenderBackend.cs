using System.Runtime.InteropServices;
using Cockpit.Core.Diagnostics;

namespace Cockpit.App.Services;

// Describes the graphics backend the cockpit draws with, for the diagnostics panel (AC-58/AC-57: "is macOS
// on Metal?"). Avalonia exposes no public API for the live backend, so this reports the configured
// preference honestly instead. Also the hook AC-57's render-backend override uses to confirm it took effect.
public static class RenderBackend
{
    public static RenderingInfo Describe()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Report the backend startup actually applied (AC-67: from the Options setting or the env var), so the
            // tester can confirm the override took effect rather than guessing. Null = no override, platform default.
            if (RenderBackendOverride.Applied is { } selection)
            {
                return new RenderingInfo(
                    $"{selection.Label} (forced via render-backend setting)",
                    "A render-backend override is active. Set it back to Auto in Options → Debug (or unset "
                    + $"{RenderBackendOverride.EnvironmentVariable}) to return to the platform default (Metal, "
                    + "with a software fallback).");
            }

            return new RenderingInfo(
                "Platform default (auto-detected)",
                "macOS defaults to Metal; it falls back to software if the GPU surface cannot be created. "
                + "No render-backend override is configured (choose Metal/OpenGL in Options → Debug to probe AC-57).");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new RenderingInfo(
                "Platform default (auto-detected)",
                "Windows defaults to Direct3D through ANGLE, falling back to software. No override is configured.");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new RenderingInfo(
                "Platform default (auto-detected)",
                "Linux/X11 defaults to OpenGL (EGL/GLX), falling back to software. No override is configured.");
        }

        return RenderingInfo.Unknown;
    }
}
