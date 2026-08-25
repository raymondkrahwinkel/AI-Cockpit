namespace Cockpit.Core.Voice;

// Where a GPU runtime's native libraries come from and where they have to land: the NuGet package
// (`PackageId`, e.g. `Whisper.net.Runtime.Cuda12.Windows`) that carries them, the folder inside it
// holding the natives (`PackageNativeFolder`, e.g. `build/win-x64`), and the cache folder below the runtime search path Whisper.net's loader looks in (`CacheSubPath`, e.g. `runtimes/cuda12/win-x64`).
public sealed record WhisperRuntimePackage(string PackageId, string PackageNativeFolder, string CacheSubPath);
