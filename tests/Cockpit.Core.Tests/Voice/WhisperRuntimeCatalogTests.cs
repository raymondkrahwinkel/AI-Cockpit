using Cockpit.Core.Voice;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// What has to be fetched for a backend to be loadable at all: which NuGet package carries its natives, where
/// the loader expects them, and which Whisper.net version they must match. The GPU runtimes are not bundled —
/// they are pulled on first use — so a wrong answer here is a silent drop to the CPU, never a crash.
/// </summary>
public class WhisperRuntimeCatalogTests
{
    /// <summary>
    /// Whisper.net.Runtime.Cuda (no OS suffix) is a meta-package holding a readme and two dependencies. Fetching
    /// it would cache an empty runtime that the loader skips without a word, so the split package is the only
    /// one that carries natives.
    /// </summary>
    [Theory]
    [InlineData(WhisperRuntimeBackend.Cuda, WhisperHostPlatform.Windows, "Whisper.net.Runtime.Cuda.Windows")]
    [InlineData(WhisperRuntimeBackend.Cuda, WhisperHostPlatform.Linux, "Whisper.net.Runtime.Cuda.Linux")]
    [InlineData(WhisperRuntimeBackend.Cuda12, WhisperHostPlatform.Windows, "Whisper.net.Runtime.Cuda12.Windows")]
    [InlineData(WhisperRuntimeBackend.Cuda12, WhisperHostPlatform.Linux, "Whisper.net.Runtime.Cuda12.Linux")]
    public void Resolve_CudaBackends_UseTheOsSplitPackageNotTheMetaPackage(
        WhisperRuntimeBackend backend, WhisperHostPlatform platform, string expectedPackageId)
    {
        var package = WhisperRuntimeCatalog.Resolve(backend, platform, "x64");

        Assert.NotNull(package);
        package.PackageId.Should().Be(expectedPackageId);
    }

    /// <summary>Vulkan is published as one package carrying both the win-x64 and linux-x64 natives.</summary>
    [Theory]
    [InlineData(WhisperHostPlatform.Windows)]
    [InlineData(WhisperHostPlatform.Linux)]
    public void Resolve_Vulkan_UsesTheSameUnsplitPackageOnEveryPlatform(WhisperHostPlatform platform)
    {
        var package = WhisperRuntimeCatalog.Resolve(WhisperRuntimeBackend.Vulkan, platform, "x64");

        Assert.NotNull(package);
        package.PackageId.Should().Be("Whisper.net.Runtime.Vulkan");
    }

    /// <summary>The CPU runtimes ship with the app, so there is nothing to fetch for them.</summary>
    [Theory]
    [InlineData(WhisperRuntimeBackend.Cpu)]
    [InlineData(WhisperRuntimeBackend.CpuNoAvx)]
    public void Resolve_CpuBackends_ResolveToNothingBecauseTheyAreBundled(WhisperRuntimeBackend backend)
    {
        WhisperRuntimeCatalog.Resolve(backend, WhisperHostPlatform.Windows, "x64").Should().BeNull();
    }

    /// <summary>
    /// No CUDA or Vulkan package carries a macOS native, so there is nothing to fetch for a Mac. Its GPU
    /// acceleration is Metal, which ships inside the bundled CPU runtime rather than as a family of its own.
    /// </summary>
    [Theory]
    [InlineData(WhisperRuntimeBackend.Cuda)]
    [InlineData(WhisperRuntimeBackend.Cuda12)]
    [InlineData(WhisperRuntimeBackend.Vulkan)]
    public void Resolve_OnMacOs_ResolvesToNothing(WhisperRuntimeBackend backend)
    {
        WhisperRuntimeCatalog.Resolve(backend, WhisperHostPlatform.MacOs, "arm64").Should().BeNull();
    }

    /// <summary>Whisper.net's loader says "macos", not the NuGet RID's "osx" — a wrong segment finds nothing.</summary>
    [Theory]
    [InlineData(WhisperHostPlatform.Windows, "win")]
    [InlineData(WhisperHostPlatform.Linux, "linux")]
    [InlineData(WhisperHostPlatform.MacOs, "macos")]
    public void PathSegment_UsesTheLoadersOwnPlatformNames(WhisperHostPlatform platform, string expected)
    {
        WhisperRuntimeCatalog.PathSegment(platform).Should().Be(expected);
    }

    /// <summary>
    /// The two ends of the copy: the natives sit flat under build/{rid} in the package, and the loader only
    /// looks under runtimes/{family}/{platform}-{arch} — its own scheme, not the NuGet RID layout.
    /// </summary>
    [Fact]
    public void Resolve_MapsThePackageFolderOntoTheLayoutTheLoaderSearches()
    {
        var package = WhisperRuntimeCatalog.Resolve(WhisperRuntimeBackend.Cuda12, WhisperHostPlatform.Windows, "x64");

        Assert.NotNull(package);
        package.PackageNativeFolder.Should().Be("build/win-x64");
        package.CacheSubPath.Should().Be(Path.Combine("runtimes", "cuda12", "win-x64"));
    }

    [Fact]
    public void Resolve_Vulkan_LandsInItsOwnRuntimeFamilyFolder()
    {
        var package = WhisperRuntimeCatalog.Resolve(WhisperRuntimeBackend.Vulkan, WhisperHostPlatform.Linux, "x64");

        Assert.NotNull(package);
        package.PackageNativeFolder.Should().Be("build/linux-x64");
        package.CacheSubPath.Should().Be(Path.Combine("runtimes", "vulkan", "linux-x64"));
    }

    /// <summary>
    /// The trap this exists for: Whisper.net runs Path.GetDirectoryName over LibraryPath, so a plain directory
    /// resolves to its parent and every fetched runtime goes unfound — on the CPU, without an error.
    /// </summary>
    [Fact]
    public void ToLibrarySearchPath_ResolvesBackToTheDirectoryItselfNotItsParent()
    {
        var runtimeRoot = Path.Combine(Path.GetTempPath(), "Cockpit", "whisper-runtimes", "1.9.1");

        var searchPath = WhisperRuntimeCatalog.ToLibrarySearchPath(runtimeRoot);

        Path.GetDirectoryName(searchPath).Should().Be(runtimeRoot);
    }

    [Fact]
    public void ToLibrarySearchPath_IsIdempotentWhenTheSeparatorIsAlreadyThere()
    {
        var runtimeRoot = Path.Combine(Path.GetTempPath(), "Cockpit") + Path.DirectorySeparatorChar;

        WhisperRuntimeCatalog.ToLibrarySearchPath(runtimeRoot).Should().Be(runtimeRoot);
    }

    /// <summary>
    /// Build metadata is not part of a NuGet version, so it has to come off or the fetch 404s. A prerelease
    /// suffix does have to survive — that is the part the assembly version drops, which would quietly fetch
    /// natives from a different build than the library loading them.
    /// </summary>
    [Theory]
    [InlineData("1.9.1+98278acc38ae23590cdfa9859f78f089abae52a7", "1.9.1")]
    [InlineData("1.9.1-preview2+abc123", "1.9.1-preview2")]
    [InlineData("1.9.1-preview2", "1.9.1-preview2")]
    [InlineData("1.9.1", "1.9.1")]
    public void NormalizePackageVersion_DropsBuildMetadataAndKeepsPrerelease(string informational, string expected)
    {
        WhisperRuntimeCatalog.NormalizePackageVersion(informational).Should().Be(expected);
    }
}
