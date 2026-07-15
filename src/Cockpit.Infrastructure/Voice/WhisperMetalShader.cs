using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Points ggml at the Metal shader on macOS — the one thing standing between an Apple Silicon Mac and its GPU.
/// <para>
/// Metal is not a runtime we fetch: it rides inside the bundled CPU runtime
/// (<c>libggml-metal-whisper.dylib</c>). But that dylib compiles its kernels from <c>ggml-metal.metal</c> at
/// load time — Whisper.net ships no precompiled <c>default.metallib</c> — and it looks for that source in
/// three places, in order: <c>GGML_METAL_PATH_RESOURCES</c>, the app bundle's <c>Contents/Resources</c>, then
/// a bare relative path against the working directory.
/// </para>
/// <para>
/// <c>Whisper.net.Runtime.Metal</c> (a transitive dependency of the CPU runtime, so it is already in the
/// publish) copies the shader next to the binary — which in a <c>.app</c> is <c>Contents/MacOS</c>, not
/// <c>Resources</c>, and a Finder-launched app's working directory is <c>/</c>. So both fallbacks miss, ggml
/// logs and drops to the CPU, and a Mac with a perfectly good GPU transcribes slowly for no visible reason.
/// Naming the directory we actually ship it in is what closes that.
/// </para>
/// </summary>
internal static class WhisperMetalShader
{
    private const string PathVariable = "GGML_METAL_PATH_RESOURCES";
    private const string ShaderFileName = "ggml-metal.metal";

    /// <summary>
    /// Makes the shader findable before the first <see cref="Whisper.net.WhisperFactory"/> is built. A no-op
    /// off macOS, and where the operator has already set the variable themselves.
    /// </summary>
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
