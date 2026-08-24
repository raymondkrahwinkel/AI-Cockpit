using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Voice;

// AC-1013: Points ggml at the Metal shader on macOS, the one thing standing between an Apple Silicon Mac and
// its GPU. Whisper.net compiles kernels from `ggml-metal.metal` at load time with no precompiled metallib, and
// its two fallback search paths (`Contents/Resources`, working dir) both miss a Finder-launched `.app`, silently dropping to CPU.
internal static class WhisperMetalShader
{
    private const string PathVariable = "GGML_METAL_PATH_RESOURCES";
    private const string ShaderFileName = "ggml-metal.metal";

    // Makes the shader findable before the first `Whisper.net.WhisperFactory` is built. A no-op
    // off macOS, and where the operator has already set the variable themselves.
    public static void EnsureDiscoverable(ILogger? logger = null)
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 })
        {
            return;
        }

        var shaderDirectory = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(shaderDirectory, ShaderFileName)))
        {
            // Worth a word: without the shader Metal cannot come up, and the only other sign is a Mac that
            // transcribes at CPU speed. ggml's own failure is a log line nobody reads.
            logger?.LogWarning(
                "{Shader} is not next to the app ({Directory}); Whisper cannot compile its Metal kernels and will transcribe on the CPU",
                ShaderFileName, shaderDirectory);

            return;
        }

        Environment.SetEnvironmentVariable(PathVariable, shaderDirectory);
    }
}
