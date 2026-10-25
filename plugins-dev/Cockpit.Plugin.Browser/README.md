# Browser plugin

A web browser inside Cockpit on CEF (Chromium Embedded Framework). This README covers the engine delivery
(AC-1727) and the browser itself (AC-1711).

## The browser

- **One CEF per Cockpit process**, started on the first opening, shut down on the UI thread after every browser
  closed. Offscreen rendering into `CefBrowserView`, GPU on. Any start failure is shown on the panel with the reason.
- **The subprocess** is `Subprocess/Cockpit.Plugin.Browser.Subprocess(.exe)` in the plugin folder, with an
  `app.manifest` carrying `supportedOS` (without it, offscreen rendering freezes after the first frames of a heavy
  page). The SDK only embeds that manifest when building on Windows, so a Windows zip has to be built on Windows.
  See "Publishing per platform" below.
- **Raymond's profile** is its own `CefRequestContext` under `<state root>/browser/raymond/`. **No remote debugging
  port, ever**: it would open every tab of every profile to the whole machine.
- **One browser, two places:** a tab (`browser.tab`) and the rail (`browser.rail`, KeepAlive and CanFloat). The view
  is in one place at a time; the other says where it is.
- **Codecs:** VP9, AV1 and Opus play (YouTube); H.264, AAC, HEVC and Widevine do not. A page that asks for one gets
  a bar with "Open in system browser".
- **macOS is off** until a Mac hand test (helper apps, `CefAppProtocol`, signing).
- **Sharing a tab (AC-1714):** only from the browser, for the tab in view, through the host's consent card with
  choices (with which session; watch). A grant is one session on one tab and ends on Stop sharing, closing the tab or
  the session ending; a move to another origin pauses it until "Keep sharing on …". The `cockpit-browser` MCP
  (`browser_list_shared_tabs`, `browser_read_tab`, `browser_screenshot_tab`) answers only the transport-verified
  caller, reads in-process through `ExecuteDevToolsMethod`, and writes each read to `consent-audit.jsonl` as a
  Bypassed line with `PreApprovedBy: tab grant <id>`. No tool touches cookies, storage, other tabs or DevTools.
- **Controlling a shared tab (AC-1728):** `request_tab_control(reason, tabId?)` opens the same card on the asking
  session's behalf with Control picked; a control grant sits on the same (session, tab) key, and a tab has one
  controller. `browser_click`, `browser_type`, `browser_press_key`, `browser_scroll` and `browser_navigate` (same origin
  only) go in through CEF's OSR input (`SendMouseClickEvent`, `SendKeyEvent`), never DevTools `Input.*` or a port,
  and each lands in the consent audit; of typed text only its length and the field, never the text. The operator's own
  click or key takes over. Each action is decided again on CEF's UI thread right before it goes in (same grant, same
  origin, and for keys a focused frame on that origin); a click must land on the top document, as DevTools'
  `DOM.getNodeForLocation` says, never in a frame. While a tab is controlled, every form submission and non-GET
  navigation in any frame waits in `OnBeforeResourceLoad` (`ContinueAsync`) until the operator sends it (the very
  request continues, body and all) or drops it; one to another origin is cancelled, and so is the top frame leaving
  the origin unless the address bar sent it; no file picker opens. While a submission waits, DevTools reads of that
  tab do not answer. A page's own `fetch` is not covered: a page of the granted origin could always send data itself.
- **Headless renders:** `--screenshot <png> --scene plugin:browser-tab` (also `browser-workspace` in the main window, `browser-rail`, `browser-floating`, `browser-codec`,
  `browser-shared`, `browser-share-paused`, `browser-workspace-shared`, `browser-engine-download`, `browser-engine-missing`), with a page placeholder.

## The agent profile (AC-1713)

- **The Agents side** of the profile switch lists the tabs of every live agent browser (AC-1712) with the session as a
  badge. It reads them over the plugin channel (`agents.*`), so it works when the backend runs on a server.
- **The picture** is a CDP `Page.startScreencast` of the tab on screen, straight from the agent Chromium: no CEF. It runs
  only while the view is visible; hide the panel and it stops. The count in the rail tab is the number of agent tabs.
- **Take over / Give back / Stop agent:** Playwright MCP is pointed at `AgentCdpProxy`, not at Chromium. While Raymond
  drives, the proxy answers the session's `Input.*` commands with an error that says so; everything else passes. His
  own mouse and keys go to the tab over a separate connection, so the pause never holds them. Stop agent kills the
  Chromium and leaves the proxy up, so the session's next call reads that Raymond stopped it.
- **Known edges:** a session's `Runtime.evaluate` can still click while Raymond drives (only `Input.*` is held), and the
  rail count is refreshed on changes and while the panel is open, not by a background poll.

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

## Publishing per platform

The store has one zip per platform for this plugin: `browser-<version>-<rid>.zip` for `win-x64`, `win-arm64`,
`linux-x64` and `linux-arm64`, listed under the version's `packages` in the store index. A cockpit downloads the
zip for its own platform (host 0.118.0 and later, hence `minHostVersion`). macOS has no zip yet, so a Mac shows the
plugin as incompatible with "No package for osx-arm64" instead of installing a browser that cannot start.

- **Why per platform:** each zip carries the subprocess as a **self-contained** publish in `Subprocess/`. A user's
  machine has no .NET, and Cockpit's own self-contained folder is no `DOTNET_ROOT`, so a framework-dependent exe
  would not start. That costs about 35 MB per zip. The plugin's own DLLs stay framework-dependent and load into
  Cockpit as always; the subprocess's runtime sits in its own folder, out of the plugin's load context.
- **Where:** `store.json` names the `runtimeIdentifiers` and the `subprocess` (its path in the zip). That is read by
  `publish-plugin.yml` only; `plugin.json` does not change. The workflow publishes the Windows RIDs on a Windows
  runner (arm64 cross-published from x64, still with the manifest) and the Linux RIDs on ubuntu, then zips, hashes
  and upserts them with `--package`. Every other plugin skips the Windows job and publishes one zip as before.
- **Checked before the store PR:** `scripts/verify-plugin-packages.sh` checks every zip statically: its sha256
  against the index, `hostfxr` beside the subprocess, the Windows 10 `supportedOS` id in each `.exe`, and the exec
  bit of the Linux subprocess inside the zip. Run it locally against the four zips before changing this route:

  ```
  for rid in win-x64 win-arm64 linux-x64 linux-arm64; do
    dotnet publish plugins-dev/Cockpit.Plugin.Browser/Subprocess -c Release -r $rid --self-contained -o publish/$rid
  done
  # zip each as the workflow does, upsert with --package, then:
  bash scripts/verify-plugin-packages.sh <store dir> browser <version> Subprocess/Cockpit.Plugin.Browser.Subprocess
  ```
- **A local build** puts a framework-dependent subprocess in `bin/<Configuration>/net10.0/Subprocess/`, the same
  place the zip has it, so a dev cockpit with .NET installed runs it as before.
