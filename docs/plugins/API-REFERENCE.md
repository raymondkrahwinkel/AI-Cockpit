# Cockpit Plugin API Reference

Every type and method a plugin can call, from the one assembly you reference:
**`Cockpit.Plugins.Abstractions`**. For the how-to (project setup, manifest, packaging, install, stores),
see the [Plugin SDK guide](PLUGIN-SDK.md); this page is the method-by-method reference.

- **Contract version:** `AbstractionsContract.Version` (currently **`1`**). Your `plugin.json`'s
  `abstractionsVersion` must equal the host's major, or the host refuses to load the plugin.
- **Threading:** contribution callbacks (`Func<Control>`, `Action onInvoke`, `Save()`) run on the **UI
  thread**. `ICockpitActions` methods are async and safe to `await` from the UI thread.
- **Nullability:** the assembly is nullable-annotated; honour it.

---

## `AbstractionsContract`

```csharp
public static class AbstractionsContract
{
    public const int Version = 1;
}
```

The plugin-contract major. The host loads a plugin only when its manifest `abstractionsVersion` equals
this. The contract grows **additively** within a major (new members arrive as default interface methods on
`ICockpitHost`); a breaking change bumps `Version`.

---

## `ICockpitPlugin`

The entry point your plugin implements (`: IDisposable`). The host discovers it in your entry assembly.

```csharp
public interface ICockpitPlugin : IDisposable
{
    PluginMetadata Metadata { get; }
    void ConfigureServices(IServiceCollection services);
    void Initialize(ICockpitHost host);
}
```

### `PluginMetadata Metadata { get; }`
Identity shown in the Plugins manager. Return a `PluginMetadata` (see below). Read early — keep it a plain
property with no side effects.

### `void ConfigureServices(IServiceCollection services)`
**Phase 1**, *before* the host builds its DI container — register your own services here.
- **Parameter** `services` — the host's service collection (from `Microsoft.Extensions.DependencyInjection.Abstractions`).
- **Note:** only runs at startup for an *already-enabled* plugin. A plugin enabled *this* session contributes
  its **UI** immediately (via `Initialize`) but its **services only after the next restart** (the container is
  already built). Keep this optional where you can. Leave the body empty if you register nothing.

### `void Initialize(ICockpitHost host)`
**Phase 2**, once the host and UI exist — register your contribution points through `host` (below). This is
where the plugin actually wires itself into the cockpit.
- **Parameter** `host` — the facade described next.

### `void Dispose()` *(from `IDisposable`)*
Runs when the plugin is **disabled** or the app exits — release timers, `HttpClient`s, subscriptions, etc.
The assembly is **not** unloaded until the process restarts (a loaded plugin cannot be truly unloaded), so
"disable" means *UI removed + `Dispose` called*.

---

## `ICockpitHost`

Handed to you in `Initialize`. The contract's only intended growth surface.

```csharp
public interface ICockpitHost
{
    IServiceProvider Services { get; }
    ICockpitActions Actions { get; }
    IPluginStorage Storage { get; }
    void AddSettings(Func<Control> createView);
    void AddSideMenuButton(string title, Action onInvoke);
    void AddSideMenuSection(string title, Func<Control> createView);
    Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560);
}
```

### `IServiceProvider Services { get; }`
The built host container. Resolve services you (or the host) registered:
`host.Services.GetRequiredService<MyService>()`. Prefer resolving your own registered services over
reaching into host internals.

### `ICockpitActions Actions { get; }`
Actions on the cockpit/session — see [`ICockpitActions`](#icockpitactions).

### `IPluginStorage Storage { get; }`
Your per-plugin key/value store — see [`IPluginStorage`](#ipluginstorage).

### `void AddSettings(Func<Control> createView)`
Registers your **settings view**, opened from the **gear** next to your plugin in the Plugins manager (there
is no top-level Options tab per plugin).
- **Parameter** `createView` — a factory returning your settings `Control`, invoked on the UI thread when the
  gear is clicked.
- **Call at most once.**
- If your control implements [`IPluginSettingsView`](#ipluginsettingsview), the host's dialog shows a **Save**
  button; otherwise just **Close**.
```csharp
host.AddSettings(() => new MySettingsControl(host.Storage));
```

### `void AddSideMenuButton(string title, Action onInvoke)`
Adds a **launcher button** to the left sidebar.
- **Parameters:** `title` — the button label; `onInvoke` — runs (UI thread) when clicked, typically to open a
  dialog via `ShowDialogAsync`.
```csharp
host.AddSideMenuButton("GitHub Issues", () => _ = host.ShowDialogAsync("Issues", () => BuildIssuesView()));
```

### `void AddSideMenuSection(string title, Func<Control> createView)`
Adds an **inline accordion section** under the session list — for small, always-visible content (not a heavy
panel).
- **Parameters:** `title` — the section header; `createView` — factory for the section's `Control`.

### `Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560)`
Opens a **modal dialog** over the main window hosting your content; you own the content control.
- **Parameters:** `title` — window title; `createContent` — factory for the dialog body; `width`/`height` —
  size in DIPs (defaults 720×560).
- **Returns:** a `Task` that completes when the dialog closes.
- The host provides a themed **DataGrid** app-wide, so your content may use it.
```csharp
await host.ShowDialogAsync("Issues", () => BuildIssuesView(), width: 900, height: 600);
```

---

## `ICockpitActions`

Act on the cockpit and the running session.

```csharp
public interface ICockpitActions
{
    Task SetClipboardTextAsync(string text);
    Task InjectIntoActiveSessionAsync(string text);
    bool HasActiveSession { get; }
}
```

### `Task SetClipboardTextAsync(string text)`
Puts `text` on the system clipboard. Use as a fallback when there is no active session to inject into.

### `Task InjectIntoActiveSessionAsync(string text)`
Injects `text` into the **currently selected** session — appended to the input box for an SDK/local session,
written to the pty for a TTY session. **No-op when `HasActiveSession` is false.**
```csharp
if (host.Actions.HasActiveSession)
    await host.Actions.InjectIntoActiveSessionAsync(prompt);
else
    await host.Actions.SetClipboardTextAsync(prompt);
```

### `bool HasActiveSession { get; }`
True when a session is selected (so `InjectIntoActiveSessionAsync` will land). Check it before injecting.

---

## `IPluginStorage`

Per-plugin key/value storage, persisted in a plugin-scoped slice of the host's `cockpit.json`. Values are
JSON-serialized.

```csharp
public interface IPluginStorage
{
    T? Get<T>(string key);
    void Set<T>(string key, T value);
}
```

### `T? Get<T>(string key)`
Reads and deserializes the value for `key`, or `default(T)` (e.g. `null`) if unset. Provide a fallback:
`host.Storage.Get<string>("repo") ?? ""`.

### `void Set<T>(string key, T value)`
Serializes and persists `value` under `key`. Works for primitives and your own DTO types.
```csharp
host.Storage.Set("repo", "owner/name");
host.Storage.Set("options", new MyOptions { Token = "…", Filter = "open" });
```

---

## `IPluginSettingsView`

Optional interface your **settings control** (the one passed to `AddSettings`) implements to get a standard
**Save** button in the host's settings dialog.

```csharp
public interface IPluginSettingsView
{
    bool Save();
}
```

### `bool Save()`
Persist the settings (typically via `host.Storage`). **Return `true`** to close the dialog, **`false`** to keep
it open (e.g. validation failed). A settings view that applies changes live can skip this interface and just
gets a Close button.
```csharp
public sealed class MySettingsControl : UserControl, IPluginSettingsView
{
    public bool Save()
    {
        if (string.IsNullOrWhiteSpace(_repo.Text)) return false; // keep open
        _storage.Set("repo", _repo.Text);
        return true;                                             // close
    }
}
```

---

## `PluginMetadata`

The identity you return from `ICockpitPlugin.Metadata`.

```csharp
public sealed record PluginMetadata(string Id, string DisplayName, string Version, string? Author, string? Description);
```

| Field | Type | Meaning |
|---|---|---|
| `Id` | `string` | Stable identity (match your `plugin.json` `id`). |
| `DisplayName` | `string` | Shown in the Plugins manager. |
| `Version` | `string` | Your plugin's version. |
| `Author` | `string?` | Optional. |
| `Description` | `string?` | Optional one-liner. |

```csharp
public PluginMetadata Metadata { get; } =
    new("github-issues", "GitHub Issues", "1.0.0", "You", "Browse and inject GitHub issues.");
```

---

## Minimal plugin

```csharp
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugins.Abstractions;

public sealed class MyPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new("my-plugin", "My Plugin", "1.0.0", "You", "Does a thing.");

    public void ConfigureServices(IServiceCollection services) { /* optional */ }

    public void Initialize(ICockpitHost host)
    {
        host.AddSideMenuButton("My Plugin", () =>
            _ = host.ShowDialogAsync("My Plugin", () => new TextBlock { Text = "Hello from a plugin!" }));
    }

    public void Dispose() { }
}
```

See the [Plugin SDK guide](PLUGIN-SDK.md) for the project file, manifest, packaging and install steps, and the
[GitHub Issues plugin](../../plugins-dev/Cockpit.Plugin.GitHubIssues) for a full example.
