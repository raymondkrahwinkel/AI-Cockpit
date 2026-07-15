namespace Cockpit.Core.Voice;

/// <summary>
/// Where a GPU runtime's native libraries come from and where they have to land: the NuGet package that
/// carries them, the folder they sit in inside that package, and the folder Whisper.net's loader looks in.
/// </summary>
/// <param name="PackageId">NuGet package id, e.g. <c>Whisper.net.Runtime.Cuda12.Windows</c>.</param>
/// <param name="PackageNativeFolder">Folder inside the package holding the natives, e.g. <c>build/win-x64</c>.</param>
/// <param name="CacheSubPath">Folder below the runtime search path, e.g. <c>runtimes/cuda12/win-x64</c>.</param>
public sealed record WhisperRuntimePackage(string PackageId, string PackageNativeFolder, string CacheSubPath);
