# Browser plugin

A web browser inside Cockpit on CEF (Chromium Embedded Framework). This README covers the engine delivery
(AC-1727) and the browser itself (AC-1711).

## The browser

- **One CEF per Cockpit process**, started on the first opening, shut down on the UI thread after every browser
  closed. Offscreen rendering into `CefBrowserView`, GPU on. Any start failure is shown on the panel with the reason.
- **The subprocess** is `Cockpit.Plugin.Browser.Subprocess(.exe)` in the plugin folder, with an `app.manifest`
  carrying `supportedOS` (without it, offscreen rendering freezes after the first frames of a heavy page). The SDK
  only embeds that manifest when building on Windows, so a Windows zip has to be built on Windows.
- **Raymond's profile** is its own `CefRequestContext` under `<state root>/browser/raymond/`. **No remote debugging
  port, ever**: it would open every tab of every profile to the whole machine.
- **One browser, two places:** a tab (`browser.tab`) and the rail (`browser.rail`, KeepAlive and CanFloat). The view
  is in one place at a time; the other says where it is.
- **Codecs:** VP9, AV1 and Opus play (YouTube); H.264, AAC, HEVC and Widevine do not. A page that asks for one gets
  a bar with "Open in system browser".
- **macOS is off** until a Mac hand test (helper apps, `CefAppProtocol`, signing).
- **Headless renders:** `--screenshot <png> --scene plugin:browser-tab` (also `browser-workspace` in the main window, `browser-rail`, `browser-floating`, `browser-codec`,
  `browser-engine-download`, `browser-engine-missing`), with a page placeholder.

## How the engine arrives

The plugin does not contain CEF. On the first opening of the browser, `CefRuntimeCache` fetches the official
*minimal* distribution for this machine from `cef-builds.spotifycdn.com` into
`<state root>/browser-runtime/<cefVersion>/<rid>/`.

- **One CEF per plugin version.** `UI/Runtime/cef-catalog.json` is embedded in the plugin DLL, so the plugin's
  hash covers the URL, size and SHA-256 per platform. Trusting the plugin means trusting exactly these archives.
- **Checked before use.** The archive is hashed while it downloads. A different SHA-256 (or a download larger
  than the pinned size) is discarded: no runtime directory appears, the caller gets the reason, and the browser
  stays off.
- **Swapped in whole.** Unpacked into a `.fetching` directory next to the target and moved into place only when
  complete, so an interrupted fetch is never read as cached.
- **Cleaned up.** After a verified runtime is in place, every other version directory is removed.
- **Never on the server.** The fetch lives in the UI assembly (`Cockpit.Plugin.Browser.UI`); the backend assembly
  names no CEF and the server does not load the UI assembly.

Platforms: `win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`. macOS is catalogued but has
not been tried on hardware. The Linux runtime is not stripped (about 1.5 GB on disk for x64); the catalog carries
both the download and the unpacked size per platform, and `CefRuntimePackage.DownloadText` states both.

## Update policy

- **One CEF version per plugin version.** The binding (`CefGlue.Next.Core`, whose version *is* the CEF version;
  `CefRuntime.Load` checks it) and the catalog are raised together, in one plugin release.
- **Rhythm:** follow the stable CEF branch at every new CEF major, through a plugin release. The binding fork has
  followed a major within 10–13 days.
- **Out of rhythm:** within 7 days of a Chrome stable release that reports a vulnerability exploited in the wild,
  as soon as the CEF build and the binding exist.
- **The ceiling:** we never run ahead of the binding. If it lags, so do we.
- **Visible:** the plugin settings show the CEF and Chromium version and the build date. Older than 60 days, they
  add "Engine outdated · update the plugin". Nothing is ever blocked.

## Release process

1. Raise `CefGlue.Next.Core` to the new CEF version (when the binding exists in the UI project).
2. Regenerate the catalog: `bash scripts/generate-cef-catalog.sh <cef-version> <empty work dir>`. It reads the
   official index, downloads every minimal archive (about 1.4 GB), checks the index's SHA-1, measures the unpacked
   size, and writes our own SHA-256 into `UI/Runtime/cef-catalog.json`. Delete the work dir afterwards.
3. Raise `version` in `plugin.json`.
4. Try it per platform (hand test or CI smoke): first opening fetches, a second start fetches nothing, the old
   version directory is gone.
5. Publish the plugin (`publish-plugin.yml`) and let it update the store index.
