# Binary size & on-demand GPU runtimes

*Raised by Raymond 2026-07-15: "why is the Cockpit binary so big, and can the local-LLM / voice drivers (ROCm, CUDA, Vulkan, …) be made on-demand — kept up to date, only downloading the runtime the PC actually needs?" Built 2026-07-15.*

## TL;DR

- A self-contained `win-x64` publish was **1.8 GB** and is now **294 MB** — measured before and after on the same machine with the flags `release.yml` uses. The difference is the **~1.2 GB of Whisper GPU native runtimes** (CUDA + CUDA12 + Vulkan) that a self-contained publish copied into the output for every platform.
- Those runtimes are now **fetched on first dictation** and cached under `%APPDATA%/Cockpit/whisper-runtimes/<whisperNetVersion>/` — only the one this machine can actually use. The **CPU runtimes stay bundled** as the floor transcription always falls back to.
- The **local-LLM providers (Ollama / LM Studio) are not bundled at all** — external servers the user installs, reached over HTTP. They add no driver weight. The only heavy "drivers" were the Whisper GPU runtimes.
- **ROCm is not a factor**: Whisper.net publishes no ROCm runtime, so AMD-on-Linux falls back to CPU. Nothing to trim or fetch.

## Why a build flag was the wrong answer

An earlier iteration of this branch shipped `-p:BundleGpuWhisperRuntimes=false`: a build-time opt-out that dropped the GPU runtimes for a slim, CPU-only publish. It has been removed, and it is worth writing down why so it is not proposed again (Raymond, 2026-07-15: *"gaat tegen de auto option in / sloopt hem waarschijnlijk zelfs"*).

Which GPU a machine has **is not knowable at build time**. A flag decides it for a machine nobody has seen. `WhisperBackendPlanner`'s `auto` exists precisely to decide it per machine at load time — and a slim build hollows that out: `auto` can only choose from what was shipped, so it "detects" its way to CPU every time, while the Options backend dropdown still offers CUDA/Vulkan that cannot work there. The flag did not make `auto` crash; it made it **pointless**, silently. Users see a flag they never set, only slow transcription.

Per-backend flags (`BundleCudaWhisperRuntime` etc.) are the same mistake sliced thinner.

## Why it was big — measured

The weight is native, not managed:

```
Whisper.net.Runtime          ~68 MB   CPU (AVX) — bundled, the universal fallback
Whisper.net.Runtime.NoAvx    ~10 MB   CPU without AVX — bundled
Whisper.net.Runtime.Cuda.Windows     286 MB  ─┐
Whisper.net.Runtime.Cuda12.Windows   779 MB  ─┼─ ~1.2 GB, was bundled on every non-macOS publish
Whisper.net.Runtime.Vulkan           151 MB  ─┘
```

On any single machine **at most one** of those GPU runtimes is ever loaded — the rest was dead weight carried for portability. The model itself (`large-v3-turbo`, ~1.6 GB) was already on-demand via `WhisperModelCache`; the runtimes were the last thing bundled-for-all instead of fetched-for-one.

## How it works now

`WhisperRuntimeCache.EnsureAvailableAsync` runs in `WhisperSpeechToTextService` just before the factory is built:

1. **`WhisperBackendPlanner`** yields the try-order for the operator's preference + OS (unchanged).
2. **`WhisperGpuProbe`** answers, per backend, whether this machine can use it — *before* spending a download. It mirrors Whisper.net's own `CudaHelper`: load `cudart64_13`/`cudart64_12` (or `libcudart.so.13`/`.12`), check the major version matches, check the device count. Probing before downloading is not circular: cudart comes from a system CUDA install, not from the runtime packages.
3. **`WhisperRuntimeCatalog`** maps the first usable backend to its NuGet package and target layout.
4. **The fetch** downloads that one `.nupkg` from `api.nuget.org/v3-flatcontainer`, extracts its natives into a staging dir and swaps it in whole.
5. **`RuntimeOptions.LibraryPath`** points the loader at the cache, then `WhisperFactory.FromPath` loads as usual.

Nothing usable, or the fetch fails → the bundled CPU runtime carries it. A missing GPU runtime is slower, never fatal.

### Four things the loader's source dictates

Read from `sandrohanea/whisper.net` at tag **`1.9.1`** (note: no `v` prefix, and paths have no `src/`), because each of these silently ends on the CPU if guessed wrong:

- **`Whisper.net.Runtime.Cuda` / `.Cuda12` are meta-packages.** They hold a readme and a dependency on `.Windows`/`.Linux`. Fetching them caches an empty runtime. The split packages carry the natives, under `build/{rid}/` — *not* the NuGet `runtimes/**/native/` layout.
- **It is not one DLL.** The loader opens a dependency chain out of the same directory — `ggml-base-whisper`, `ggml-cpu-whisper`, `ggml-cuda-whisper`, `ggml-whisper`, then `whisper` — and a missing link makes it move on to the next backend without a word.
- **`LibraryPath` is a *file* path.** `NativeLibraryLoader` runs `Path.GetDirectoryName()` over it, so a bare directory resolves to its parent and nothing is ever found. `WhisperRuntimeCatalog.ToLibrarySearchPath` appends the separator; a test pins the round-trip.
- **Backend is the outer loop, search path the inner one.** So the cache dir does not hijack the order: per backend it tries the cache, then the app dir. `RuntimeLibraryOrder` stays authoritative and the bundled CPU runtimes next to the exe keep working — which is what makes `auto` remain meaningful.

Expected layout: `<LibraryPath dir>/runtimes/{cuda|cuda12|vulkan}/{win|linux}-{x64|arm64}/` (CPU has no family segment).

### Keeping it current

The version is read from the **Whisper.net assembly's own informational version** (`1.9.1+<sha>` → `1.9.1`), not from asking NuGet for the newest. The natives must match the library that loads them: a mismatch is exactly the bug this avoids — CUDA-13 natives on a CUDA-12.8 host detect it and fall silently back to CPU, which is worse than an old version because it is invisible. Bumping the `Whisper.net` package moves the fetch with it, automatically.

The version is part of the cache path, so a bump needs no migration: the old natives are not stale, they are simply not where the new loader looks. They are deleted once the new runtime is in place.

## Per platform — checked against the packages, not the README

The planner's job is to say what a host *could* load. It used to answer from a `bool isWindows`, which cannot
say "macOS": every caller read "not Windows" as Linux. Both of the resulting claims were wrong, and both cost
the operator their GPU in the way this whole page is about — silently.

| Host | Order `auto` builds | Fetched |
|---|---|---|
| Windows | Cuda → Cuda12 → Vulkan → Cpu → CpuNoAvx | the first the probe calls usable |
| Linux | Cuda → Cuda12 → Vulkan → Cpu → CpuNoAvx | idem |
| macOS | Cpu | nothing — see below |

- **Vulkan on Linux exists.** `Whisper.net.Runtime.Vulkan` 1.9.1 ships `linux-x64` natives beside `win-x64`
  (one unsplit package; verified in the real nupkg). The planner called Vulkan Windows-only, citing issue
  #264, so `auto` on Linux never offered it — an AMD-on-Linux box transcribed on the CPU with a working
  runtime one download away.
- **macOS fetches nothing, and does not need to.** No CUDA or Vulkan package carries a macOS native. Its GPU
  path is **Metal**, which is not a `RuntimeLibrary` at all — `libggml-metal-whisper.dylib` rides *inside* the
  bundled CPU runtime (`macos-arm64` only). So on Apple Silicon the CPU entry is already the GPU one. An Intel
  Mac (`macos-x64`) has no Metal native and genuinely is on the CPU.
- **NoAvx is not published for macOS** (win-x64, win-x86, linux-x64 only), so offering it there was a dead
  entry — a fallback that cannot be found is not a fallback.

### The Metal shader — the one thing macOS did need

`libggml-metal-whisper.dylib` compiles its kernels from `ggml-metal.metal` **at load time**: Whisper.net ships
no precompiled `default.metallib`, so ggml goes looking for the source. Its lookup
(`ggml/src/ggml-metal/ggml-metal-device.m`) is, in order: `GGML_METAL_PATH_RESOURCES` → the app bundle's
`Contents/Resources` → a bare relative path against the working directory.

`Whisper.net.Runtime.Metal` is a **transitive dependency of `Whisper.net.Runtime`**, so the shader is already
in every publish (450 KB, next to the binary — it even ships on Windows, where it is inert). But next to the
binary in a `.app` is `Contents/MacOS`, not `Resources`, and a Finder-launched app's working directory is `/`.
Both fallbacks miss, ggml logs a line nobody reads, and Metal drops to the CPU.

`WhisperMetalShader.EnsureDiscoverable` sets `GGML_METAL_PATH_RESOURCES` to the directory the shader actually
ships in, before the first factory. No download, no bundling change — the file was always there, just unfindable.

⚠️ **Not verified on hardware:** there is no Mac here. The lookup order is read from ggml's source and the
shader's location is measured in a real publish, but no one has watched a Mac come up on Metal.

## Verified (2026-07-15, this machine — AMD GPU, Windows)

- Publish `win-x64`, `release.yml` flags: **1.8 GB → 294 MB**.
- Live, from an empty cache: probe reported `Cuda=False, Cuda12=False, Vulkan=True` → fetched `Whisper.net.Runtime.Vulkan` → **loader reported `LoadedLibrary = Vulkan`**, not CPU. Extracted files byte-identical to the NuGet package; no `.download`/`.fetching` leftovers.
- Second run hit the cache (no download), and a planted `1.8.0/` cache directory was removed.
- ⚠️ **The CUDA path is not live-proven yet** — this machine has an AMD card. It needs the RTX-4070 Fedora host. The probe's CUDA branch mirrors `CudaHelper` line for line, but mirroring is an argument, not a measurement.

## Open

- **CoreML (macOS).** `Whisper.net.Runtime.CoreML` exists for 1.9.1 (~5.7 MB) and would *complement* Metal rather than replace it — the encoder moves to the Neural Engine while the rest stays on Metal. It needs a `.mlmodelc` beside the ggml model, which `WhisperGgmlDownloader.GetEncoderCoreMLModelAsync` fetches precompiled (no Python conversion), so it could be made automatic. Not built: no benchmark exists for what it actually buys, and nothing here can run a Mac to find out. It is an enhancement, not a defect.
- **Nobody has run a Mac.** The macOS path is reasoned from ggml's source and a measured publish, not observed. First Mac to run this should check the log for `GGML_METAL_PATH_RESOURCES` and a backend that is not the CPU.
- **Package source.** The fetch depends on nuget.org. A mirror we control would let us pin and verify a SHA the way the plugin store already does.
- **macOS Gatekeeper.** Fetched dylibs could pick up a quarantine xattr. Moot today — macOS fetches nothing — but it becomes real the moment CoreML lands.
