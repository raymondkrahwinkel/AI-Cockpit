using System.IO.Compression;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;

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

        Assert.False(File.Exists(Path.Combine(_root, "escaped.dll")));
        Assert.Equivalent(new[] { "escaped.dll" }, Directory.GetFiles(staging).Select(Path.GetFileName));
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
