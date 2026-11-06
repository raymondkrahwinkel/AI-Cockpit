Disposable Windows transport probe for AC-2142, based on release/2026-11-bundel at 145cd2ccdf803d0a0f2d0c740b8f7bb629c00a69. Target: epic/AC-2140-browser-host.

Work in progress; not ready to merge. Production code and requiresRestart are unchanged. The probe compares an instrumented source copy of CefBrowserView with shared BGRA buffers, dirty updates and frame-ready signaling. A reconstructed file-backed polling route is the negative performance control. Pixel event IDs are linked to frame IDs at the bitmap copy.

Validation is incomplete. The initial local/shared functional run passed with 40 typing and 40 scrolling events per route. The extended Release build and four-scenario functional matrix are pending. This is not definitive performance evidence: the final comparison requires a quiet-machine signal from cockpit-assistant. The missing-frame-ready mutation, Linux port and local push gates remain open.

No new test methods, cases or journeys; no test-count ceiling, workflow or nightly change.

Evidence and current status: Depot krahwinkel-it/cockpit, Reviews/2026-11-06-AC-2142-prestatiepoort/README.md. No decision to retain probe code has been requested or granted.
