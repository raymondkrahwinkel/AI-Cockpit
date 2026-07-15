using System.IO.Compression;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Voice;

/// <summary>
/// The two halves of the runtime cache that can be exercised without a network or a GPU: pulling the natives
/// out of a .nupkg, and giving the disk back after a Whisper.net bump. Both fail quietly in production — a
/// runtime that lands wrong is skipped in silence and transcription just runs slower — so they are worth
/// pinning rather than trusting to the one live run that happened to be on this machine's hardware.
/// </summary>
public sealed class WhisperRuntimeCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cockpit-runtime-cache-{Guid.NewGuid():N}");

    private static readonly WhisperRuntimePackage Cuda12Windows =
        new("Whisper.net.Runtime.Cuda12.Windows", "build/win-x64", "runtimes/cuda12/win-x64");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// The loader opens a dependency chain out of one flat directory, so every native in the package has to
    /// arrive — and arrive flattened, since the package nests them under build/{rid} and the loader does not.
    /// </summary>
    [Fact]
    public void ExtractNatives_TakesEveryNativeAndFlattensThePackageFolderAway()
    {
        var package = _CreatePackage(
            ("build/win-x64/ggml-base-whisper.dll", "base"),
            ("build/win-x64/ggml-cuda-whisper.dll", "cuda"),
            ("build/win-x64/whisper.dll", "whisper"));
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);

        var extracted = WhisperRuntimeCache.ExtractNatives(package, Cuda12Windows, staging);

        extracted.Should().Be(3);
        Directory.GetFiles(staging).Select(Path.GetFileName)
            .Should().BeEquivalentTo("ggml-base-whisper.dll", "ggml-cuda-whisper.dll", "whisper.dll");
    }

    /// <summary>A .nupkg also carries a nuspec, a readme and signatures; none of that belongs next to the natives.</summary>
    [Fact]
    public void ExtractNatives_LeavesEverythingOutsideTheNativeFolderAlone()
    {
        var package = _CreatePackage(
            ("build/win-x64/whisper.dll", "whisper"),
            ("content/readme.md", "readme"),
            ("build/Whisper.net.Runtime.Cuda12.Windows.targets", "targets"),
            ("build/linux-x64/libwhisper.so", "the other platform"));
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);

        var extracted = WhisperRuntimeCache.ExtractNatives(package, Cuda12Windows, staging);

        extracted.Should().Be(1);
        Directory.GetFiles(staging).Select(Path.GetFileName).Should().BeEquivalentTo("whisper.dll");
    }

    /// <summary>
    /// The meta-package trap: Whisper.net.Runtime.Cuda12 (no OS suffix) holds a readme and two dependencies and
    /// no natives at all. Fetching it must report nothing extracted, so the caller says so and stays on the CPU
    /// rather than caching an empty directory the loader would skip without a word.
    /// </summary>
    [Fact]
    public void ExtractNatives_ReportsNothingForAMetaPackageWithNoNatives()
    {
        var package = _CreatePackage(
            ("content/readme.md", "readme"),
            ("Whisper.net.Runtime.Cuda12.nuspec", "nuspec"));
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);

        WhisperRuntimeCache.ExtractNatives(package, Cuda12Windows, staging).Should().Be(0);
    }

    /// <summary>
    /// A package is a zip from the internet, so a traversing entry name must not be able to write outside the
    /// staging directory. Only the entry's file name is ever joined onto the path, which is what makes it safe.
    /// </summary>
    [Fact]
    public void ExtractNatives_CannotBeMadeToWriteOutsideTheStagingDirectory()
    {
        var package = _CreatePackage(("build/win-x64/../../../escaped.dll", "hostile"));
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);

        WhisperRuntimeCache.ExtractNatives(package, Cuda12Windows, staging);

        File.Exists(Path.Combine(_root, "escaped.dll")).Should().BeFalse();
        Directory.GetFiles(staging).Select(Path.GetFileName).Should().BeEquivalentTo("escaped.dll");
    }

    /// <summary>
    /// After a Whisper.net bump the old natives are hundreds of megabytes nothing will read again — the version
    /// is part of the path the loader searches, so they are unreachable rather than merely stale.
    /// </summary>
    [Fact]
    public void RemoveOtherVersions_KeepsTheVersionInUseAndDropsTheRest()
    {
        Directory.CreateDirectory(Path.Combine(_root, "1.9.1", "runtimes"));
        Directory.CreateDirectory(Path.Combine(_root, "1.8.0", "runtimes"));
        Directory.CreateDirectory(Path.Combine(_root, "1.7.6", "runtimes"));

        WhisperRuntimeCache.RemoveOtherVersions(_root, "1.9.1", logger: null);

        Directory.GetDirectories(_root).Select(Path.GetFileName).Should().BeEquivalentTo("1.9.1");
    }

    /// <summary>Nothing has been fetched yet on a fresh install; that is not a failure to clean up.</summary>
    [Fact]
    public void RemoveOtherVersions_DoesNothingWhenNothingHasEverBeenCached()
    {
        var absent = Path.Combine(_root, "never-created");

        var removing = () => WhisperRuntimeCache.RemoveOtherVersions(absent, "1.9.1", logger: null);

        removing.Should().NotThrow();
    }

    private string _CreatePackage(params (string EntryPath, string Content)[] entries)
    {
        Directory.CreateDirectory(_root);
        var packageFile = Path.Combine(_root, $"package-{Guid.NewGuid():N}.nupkg");

        using (var archive = ZipFile.Open(packageFile, ZipArchiveMode.Create))
        {
            foreach (var (entryPath, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(entryPath).Open());
                writer.Write(content);
            }
        }

        return packageFile;
    }
}
