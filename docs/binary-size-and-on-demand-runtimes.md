# Binary size & on-demand GPU runtimes

*Investigation + design. Raised by Raymond 2026-07-15: "why is the Cockpit binary so big, and can the local-LLM / voice drivers (ROCm, CUDA, Vulkan, …) be made on-demand — kept up to date, only downloading the runtime the PC actually needs?"*

## TL;DR

- The nightly self-contained build is **~968 MB**. The dominant cost is **~1.4 GB of Whisper GPU native runtimes** (CUDA + CUDA12 + Vulkan) that a self-contained publish copies into the output **for every platform**, plus the ~150 MB self-contained .NET/Avalonia runtime. (The on-disk 968 MB is after single-file compression of that payload.)
- The **local-LLM providers (Ollama / LM Studio) are not bundled at all** — they are external servers the user installs and runs themselves, reached over HTTP. So they add no driver weight. The only heavy "drivers" in the binary are the **Whisper (voice) GPU runtimes**.
- **ROCm is not a factor**: Whisper.net publishes no ROCm runtime, so AMD-on-Linux already falls back to CPU (see the `Cockpit.Infrastructure.csproj` comment). There is nothing to trim or fetch for ROCm today.
- **Now shipped (this branch):** a build-time opt-out `-p:BundleGpuWhisperRuntimes=false` that drops the 1.4 GB and produces a slim, CPU-only build. Default is unchanged.
- **Designed next (needs a GPU + slim-build validation before wiring):** fetch only the matching GPU runtime on first use, cache it next to the model, and keep it current — the same lazy pattern `WhisperModelCache` already uses for the 1.6 GB model.

## Why it is big — measured

The weight is native, not managed. From `src/Cockpit.Infrastructure/Cockpit.Infrastructure.csproj`:

```
Whisper.net.Runtime          ~68 MB   CPU (AVX) — every platform, needed as the universal fallback
Whisper.net.Runtime.NoAvx    ~10 MB   CPU without AVX — every platform
Whisper.net.Runtime.Cuda    ─┐
Whisper.net.Runtime.Cuda12  ─┼─ ~1.4 GB CUDA + Vulkan GPU natives, bundled on every non-macOS publish
Whisper.net.Runtime.Vulkan  ─┘
```

A self-contained publish resolves every referenced runtime's `runtimes/<rid>/native/*` into the output. Whisper.net's `NativeLibraryLoader` then picks the first one that actually loads on the host at model-load time (`RuntimeOptions.RuntimeLibraryOrder`, ordered by `WhisperBackendPlanner`). So on any single machine, **at most one** of those GPU runtimes is ever loaded — the rest are dead weight carried for portability.

The model itself (`large-v3-turbo`, ~1.6 GB) is **already** on-demand: `WhisperModelCache.EnsureDownloadedAsync` fetches it on first dictation into `%APPDATA%/Cockpit/models`, never bundled. The GPU runtimes are the remaining thing that is bundled-for-all instead of fetched-for-one.

## The design: fetch the runtime the PC needs

Mirror the model-cache pattern for the native runtime.

1. **Ship CPU-only by default.** Keep `Whisper.net.Runtime` (+ NoAvx) bundled — it is small (~78 MB), works everywhere, and guarantees voice always functions with zero network. Drop the CUDA/Vulkan packages from the publish (the `BundleGpuWhisperRuntimes=false` lever below already does this).
2. **Detect the hardware once.** `WhisperBackendPlanner.BuildOrder(preference, isWindows)` already yields the ordered candidate list (Cuda12 → Cuda → Vulkan → Cpu → NoAvx per host). The first *GPU* entry is the runtime to provision.
3. **Provision on first use.** A `WhisperRuntimeCache`, parallel to `WhisperModelCache`:
   - target dir `%APPDATA%/Cockpit/runtimes/<backend>-<whisperNetVersion>/`;
   - if absent, download that one runtime's native payload (the `Whisper.net.Runtime.Cuda12` etc. NuGet package for the host RID, or a mirror we host) and extract `runtimes/<rid>/native/*` into the dir;
   - point the loader at it before `WhisperFactory.FromPath` (Whisper.net resolves natives next to the app / on the native search path — the cache dir is prepended to the DLL search path on Windows via `AddDllDirectory`, and to `LD_LIBRARY_PATH`/`dlopen` handling on Linux; **this seam is the part that must be validated on a real GPU host — see Risks**).
4. **Keep it current.** The cache key includes the Whisper.net version, so bumping the package invalidates the old runtime and re-provisions. A stale/partial download uses the same `*.download` temp-then-move guard `WhisperModelCache` already uses.
5. **Always degrade to CPU.** If provisioning fails (offline, mirror down), the bundled CPU runtime still loads — voice keeps working, slower. The failure is logged (same observability fix as `fix/voice-observability`), never silent.

Net effect: base binary drops by ~1.4 GB; an NVIDIA box fetches ~0.9 GB of CUDA once; a CPU-only box fetches nothing; a Mac (already CPU-only) is unchanged.

### Local-LLM providers

No change needed for size — Ollama/LM Studio are user-installed and external. The relevant robustness (endpoint auto-detect + graceful fallback when the server is absent) already lives in `LocalLlmEndpointResolver` / `OpenAiCompatTranscriptCleanupService`. The "user provides Ollama or LM Studio themselves" contract stays: Cockpit never bundles or installs them.

## What this branch ships

`-p:BundleGpuWhisperRuntimes=false` — a reversible, default-off build lever that excludes the CUDA/Vulkan runtime packages, producing a slim CPU-only publish. Default `true` keeps today's behaviour byte-for-byte. This is the build-time half; it also de-risks the design by proving the "drop the runtimes, CPU still works" half independently.

```bash
# slim, CPU-only (no ~1.4 GB GPU natives)
dotnet publish src/Cockpit.App -c Release -r win-x64 --self-contained -p:BundleGpuWhisperRuntimes=false
```

## Risks / what to validate before wiring the runtime fetch

- **Native load path from a non-default dir.** Whisper.net 1.9's loader walks `RuntimeLibraryOrder` but resolves the actual `.dll`/`.so` from the app's runtime dirs. Loading a CUDA runtime from an arbitrary cache dir needs the OS loader pointed there first (`AddDllDirectory` + `SetDefaultDllDirectories` on Windows; `dlopen` search / rpath on Linux). This must be proven on Raymond's RTX-4070 host with a slim build before the fetch path replaces the bundled runtimes — it is the one step that can't be unit-tested.
- **Package source.** Pulling the runtime nupkg at runtime means depending on nuget.org (or a mirror we control). A hosted mirror is more predictable and lets us pin/verify a SHA the way the plugin store already does.
- **First-use latency.** A GPU box waits once for ~0.9 GB on the first dictation — same UX as the model download, so the same "downloading, first use" logging (now in `WhisperModelCache`) should cover the runtime fetch too.
