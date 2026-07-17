using System.Runtime.InteropServices;
using Cockpit.Core.Diagnostics;

namespace Cockpit.App.Services;

/// <summary>
/// Describes the graphics backend the cockpit draws with, for the diagnostics panel (AC-58) — the line AC-57 kept
/// asking for ("is macOS on Metal?"). Avalonia exposes no public API for the <em>live</em> backend, so this reports
/// the configured preference honestly rather than claiming an active backend it cannot observe: the app configures
/// nothing beyond <c>UsePlatformDetect()</c> today, so the mode is the platform default, and the detail says which
/// backend that default resolves to per OS.
/// <para>
/// This is the hook AC-57's deferred render-backend override will use: when an explicit rendering mode is set, this
/// is where it becomes visible, so the tester can confirm the override took effect.
/// </para>
/// </summary>
public static class RenderBackend
{
    public static RenderingInfo Describe()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // When AC-57's probe is active, report the forced backend so the tester can confirm the override
            // actually took effect (the hook this class was built for) rather than guessing from the env var.
            if (RenderBackendOverride.FromEnvironment() is { } selection)
            {
                return new RenderingInfo(
                    $"{selection.Label} (forced via {RenderBackendOverride.EnvironmentVariable})",
                    "A render-backend override is active for AC-57 diagnostics. Unset the variable to return to "
                    + "the platform default (Metal, with a software fallback).");
            }

            return new RenderingInfo(
                "Platform default (auto-detected)",
                "macOS defaults to Metal; it falls back to software if the GPU surface cannot be created. "
                + $"No render-backend override is configured (set {RenderBackendOverride.EnvironmentVariable}"
                + "=opengl|software to probe AC-57).");
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
