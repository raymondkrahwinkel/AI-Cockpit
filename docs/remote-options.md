# Options in a remote window (AC-2278)

The ordinary Options dialog edits typed stores. Only Admin connect keys can read or write `/api/v1/options/{runs,sessions,clones,worktrees,notifications,diagnostics,shell}`. Docker cleanup reuses `/api/v1/storage/settings`. Unknown fields and invalid values are refused. Hidden store fields stay on the server. The webhook is write-only; the existing server-secret marker preserves it, an empty value clears it.

All server groups are saved before client presentation. A server failure keeps the dialog staged, shows the reason on the affected Options page, and writes no client presentation. Successful groups can already be committed, as in the ordinary Options section-save behavior. Cancel restores confirmed backend values even without a connection, and reloads client presentation. API writes share an edit gate, including cleanup, so preserving a hidden field cannot undo another concurrent API write. No settings keys are added; README key documentation and SettingsCarrierWhitelist are unchanged.

Client presentation uses only the remote window's own state root, never the local Cockpit registry. A server shell must be empty (OS default) or the ID/absolute executable path of a shell detected on the server; arbitrary programs are refused. Host backup/image operations remain outside this feature. The later epic subtickets own Security/Swarm, profiles/assistant, MCP and plugin administration.

## Security and Swarm (AC-2279)

Only Admin keys can read or write `/api/v1/options/{terminal-access,shell-access}` and `/api/v1/options/swarm`, or post `/api/v1/options/swarm/{reset,leadership,retry,members/{id}/remove}`. Every write and action is audited, waits on the shared edit gate, and checks the calling key again under that gate, so a revoke or downgrade while it waits stops the change. The server's organisation policy still refuses enabling terminal or shell access, and the server flips the live switch itself.

An extra internal CIDR the server does not already hold must lie fully inside 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 100.64.0.0/10 (carrier-grade NAT, e.g. a VPN) or fc00::/7; anything else is refused, so no remote window makes a public address internal. The server's node listener ("Accept connections") has no route: a headless server keeps it on, and turning it off would lock the window out. Pairing (find, add by address, incoming confirmation) is a conscious gap: it stays on the server's own screen. The window never joins the swarm and receives no identity or private key; member public keys only identify the rows. A server without Swarm answers `404 no_swarm`.

Encryption, screen lock and Windows Hello protect the client device and are hidden in a remote window; its local security settings are not touched. The Servers page of a remote window shows the server's own connect keys over the existing `/api/v1/keys` contract. Revoking the key in use aborts its in-flight requests, including the `/events` stream, at once (`McpAuthMiddleware`).

## MCP Servers (AC-2282)

Only Admin keys can read or write `/api/v1/mcp/servers`, read `/api/v1/mcp/servers/{id}/auth-state`, or sign a server in or out (`/api/v1/mcp/servers/{id}/sign-in`, `/sign-out`). Every change is audited and waits on the shared edit gate, which checks the calling key again. The list holds the rows the ordinary MCP Servers page edits; node connections and names the server's own MCP servers use are left out and kept as stored. A save replaces exactly those rows, so a row it leaves out is removed.

An API key or header value reads as the server-secret marker. Sent back as the marker it keeps the stored value, but only while the row keeps its URL; otherwise the key must be entered again. Fields the page does not edit (certificate pin, plugin wiring, host-only scopes) are carried over from the stored row, and `cockpitHosted` or `internal` cannot be set over the API. A stdio row needs a command and takes no URL, authentication or headers; an HTTP row needs an http(s) URL; header names must be HTTP tokens and no value may hold a control character. Both transports run on the server. Testing a server is the profile editor's tool count (`/api/v1/profiles/mcp-servers/{name}/estimate`), which connects from the server. A sign-in keeps its token on the server; the window only runs the browser step on its own loopback listener.

A session start whose MCP gateway cannot be prepared stops, never falling back to the servers directly, and names a short cause such as `timeout: preparing took too long` or `upstream_http: HTTP 401 from upstream`. It never carries an exception text, URL, header or path; the server log keeps the detail.

## EditedProperties ownership

| Property | Owner |
| --- | --- |
| `AgentsPaletteEnabled` | Client presentation/device |
| `AgentsPaletteRailIcon` | Client presentation/device |
| `AutoCloseOnExit` | Server backend (AC-2278) |
| `AutomaticDockerCleanup` | Server backend (AC-2278) |
| `CheckForUpdatesOnStartup` | Client presentation/device |
| `CloneRoot` | Server backend (AC-2278) |
| `CombineQueuedMessages` | Server backend (AC-2278) |
| `DiscordNotificationsEnabled` | Server backend (AC-2278) |
| `GlobalFocusRailLayout` | Client presentation/device |
| `GlobalSingleSessionLayout` | Client presentation/device |
| `GlobalStackSessionsVertically` | Client presentation/device |
| `IdleThresholdMinutes` | Server backend (AC-2278) |
| `IncludeNightlyBuilds` | Client presentation/device |
| `LocalNotificationsEnabled` | Client presentation/device |
| `LogDiagnosticSnapshots` | Server backend (AC-2278) |
| `MemoryBudgetPercent` | Server backend (AC-2278) |
| `MaxParallelRuns` | Server backend (AC-2278) |
| `RunCpuCeilingPercent` | Server backend (AC-2278) |
| `RunMemoryCeilingPercent` | Server backend (AC-2278) |
| `MinimizeToTrayOnClose` | Client presentation/device |
| `NotifyOnCiFailure` | Server backend (AC-2278) |
| `MonitorMessagesPerHour` | Server backend (AC-2278) |
| `NotificationSoundOnNeedsAttention` | Client presentation/device |
| `NotificationSoundOnSessionFinished` | Client presentation/device |
| `NotificationSoundsEnabled` | Client presentation/device |
| `NotificationSoundVolume` | Client presentation/device |
| `NotifyOnSessionFinished` | Server backend (AC-2278) |
| `NotifyOnSessionIdle` | Server backend (AC-2278) |
| `NotifyWhenAllSessionsIdle` | Server backend (AC-2278) |
| `OpenInSimpleView` | Client presentation/device |
| `OrchestratorMcpEnabled` | Server backend (not over the API yet) |
| `PluginRestartAdviceAfter` | Server backend (AC-2278) |
| `QuickNoteGlobalHotkeyEnabled` | Client presentation/device |
| `QuickNoteHotkeyKeyName` | Client presentation/device |
| `RenderBackendSelection` | Client presentation/device |
| `ScreenshotGlobalHotkeyEnabled` | Client presentation/device |
| `ScreenshotHotkeyKeyName` | Client presentation/device |
| `ScreenshotPreviewEnabled` | Client presentation/device |
| `SelectedInputDevice` | Client presentation/device |
| `SelectedOutputDevice` | Client presentation/device |
| `SelectedReadAloudLanguage` | Client presentation/device |
| `SelectedSttLanguage` | Client presentation/device |
| `SelectedTerminalShell` | Server backend (AC-2278) |
| `SessionFinishedCustomSoundPath` | Client presentation/device |
| `SessionFinishedSound` | Client presentation/device |
| `SelectedTranscriptionModel` | Client presentation/device |
| `SelectedTtsVoice` | Client presentation/device |
| `SelectedVoiceBackendPreference` | Client presentation/device |
| `SessionIdleMinutes` | Server backend (AC-2278) |
| `ShowDebugControls` | Client presentation/device |
| `ShowTimestamps` | Client presentation/device |
| `ShowUsagePillContext` | Client presentation/device |
| `ShowUsagePillRateWindows` | Client presentation/device |
| `ShowUsagePillSessionUsage` | Client presentation/device |
| `TerminalCustomFontFamily` | Client presentation/device |
| `TerminalCustomShell` | Server backend (AC-2278) |
| `TerminalFontSelection` | Client presentation/device |
| `TerminalFontSize` | Client presentation/device |
| `NeedsAttentionCustomSoundPath` | Client presentation/device |
| `NeedsAttentionSound` | Client presentation/device |
| `ThemeMode` | Client presentation/device |
| `VoiceAutoSubmit` | Client presentation/device |
| `VoiceCustomModelName` | Client presentation/device |
| `VoiceEnabled` | Client presentation/device |
| `VoiceGlobalPushToTalk` | Client presentation/device |
| `VoiceOpenMicSilenceTimeoutMs` | Client presentation/device |
| `VoicePushToTalkKeyName` | Client presentation/device |
| `VoiceStopReadAloudLevelThreshold` | Client presentation/device |
| `VoiceStopReadAloudWhenSpeaking` | Client presentation/device |
| `VoiceDuckOtherAudio` | Client presentation/device |
| `VoiceDuckingPercent` | Client presentation/device |
| `VoiceTtsSpeed` | Client presentation/device |
| `WakeAgentsByDefault` | Server backend (AC-2278) |
| `WebhookUrl` | Server backend (AC-2278) |
| `WorktreeRoot` | Server backend (AC-2278) |
| `Security.LockWithOperatingSystem` | Client presentation/device |
| `Security.NodeEndpointEnabled` | Client presentation/device (the server keeps its listener on) |
| `Security.ShellAccessEnabled` | Server backend (AC-2279) |
| `Security.TerminalAccessEnabled` | Server backend (AC-2279) |
| `Swarm.ExtraInternalCidrsText` | Server backend (AC-2279) |
| `Swarm.ReturnChoice` | Server backend (AC-2279) |
| `AssistantOptions.AlwaysOnTop` | Client presentation/device |
| `AssistantOptions.ConsentBypassAll` | Server assistant (AC-2280) |
| `AssistantOptions.IsEnabled` | Server assistant (AC-2280) |
| `AssistantOptions.PushToTalkKeyName` | Client presentation/device |
| `AssistantOptions.SelectedReadingLevel` | Server assistant (AC-2280) |
| `AssistantOptions.SpeakReplies` | Client presentation/device |
| `AssistantOptions.WakeWordEnabled` | Client presentation/device |
| `AssistantOptions.WakeWord` | Client presentation/device |
| `AssistantOptions.WakeWordListeningSeconds` | Client presentation/device |
| `AssistantOptions.SelectedSpeechMode` | Client presentation/device |
| `AssistantOptions.SelectedSpeechMethod` | Client presentation/device |
| `AssistantOptions.SelectedSpeechProfileLabel` | Server assistant (AC-2280) |
