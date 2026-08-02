namespace Cockpit.Core.Voice;

// Where a GPU runtime's native libraries come from and where they have to land: the NuGet package that
// carries them, the folder they sit in inside that package, and the folder Whisper.net's loader looks in.
//
// `PackageId`: NuGet package id, e.g. `Whisper.net.Runtime.Cuda12.Windows`.
// `PackageNativeFolder`: Folder inside the package holding the natives, e.g. `build/win-x64`.
// `CacheSubPath`: Folder below the runtime search path, e.g. `runtimes/cuda12/win-x64`.
public sealed record WhisperRuntimePackage(string PackageId, string PackageNativeFolder, string CacheSubPath);
