# AC-179 Kind-Clusters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Deviation note:** this plan omits full code-per-step blocks that the writing-plans template normally requires. The ticket (AC-179) already specifies, per criterion, which existing file to copy which pattern from — that research is folded into each task's "Pattern to copy" line instead of being duplicated as prose here. The executor (this session) already holds that research in context.

**Goal:** Let an agent spin up, list and tear down a disposable local `kind` Kubernetes cluster from the cockpit, wired straight into the existing Kubernetes plugin so it is immediately usable by the plugin's own tools (`helm_upgrade`, `get_resource`, etc.) — closing the gap that cost AC-1061 two manual cluster setups.

**Architecture:** Everything lands inside `plugins-dev/Cockpit.Plugin.Kubernetes` (D1, decided). A new generic `Cli/` process-runner is extracted from `Helm/HelmRunner.cs` so `kind` and `helm` share one 89-line engine instead of two. A new `Kind/KindClusterManager.cs` owns the kind-cluster registry (persisted via the existing `IPluginStorage`-backed `KubernetesSettings`), runs create/delete through the shared CLI runner, registers a `ClusterRegistration` so the existing Kubernetes-plugin tools can reach the cluster immediately, sweeps orphans at startup exactly like `WorktreeManager.ReconcileAsync`, enforces a TTL backstop like `PortForwardManager.Start`, and shows running clusters in the status bar via `ISupervisedActivitySource`.

**Tech Stack:** C# / .NET, Avalonia (settings UI, code-behind not XAML), xUnit + NSubstitute (tests), `kind` CLI (external, not managed by Cockpit).

**Spec:** AC-179 (YouTrack) — full description, 14 acceptance criteria, D1/D2 decisions, pitfalls, out-of-scope list. Read via `mcp__YouTrack__Personal__get_issue("AC-179")`.

## Global Constraints

- D1 (decided): everything in `Cockpit.Plugin.Kubernetes`, no new plugin.
- D2 (decided): non-pinned kind clusters are torn down on cockpit close (`Dispose`, bounded + best-effort like `KubernetesPlugin.Dispose()`), except pinned ones. Orphan sweep at startup covers the crash path.
- Orphan = owner pane not in the live set (`ICockpitHost.Sessions.OpenSessions`), **never** age-based. Mirrors `WorktreeManager.ReconcileAsync`.
- Ownership check for destructive MCP calls uses `ICockpitHost.CurrentMcpCallerPaneId` (transport-verified), never the agent-supplied `session` argument, except as a fallback when the verified value is null — mirrors `WorktreeTools.RemoveAsync` / the `host.CurrentMcpCallerPaneId ?? session` pattern used across `DiagramMcpTools`/`WireframeMcpTools`.
- Safety valve: only clusters in the plugin's own registry are ever listed, pinned, expired or deleted. A cluster made outside the cockpit (`docker`/`kind` by hand) is invisible to every kind-* tool and the sweep.
- Every destructive kind action guards on the context name starting with `kind-` (mirrors `HelmUpgradeClusterTests._Kubeconfig()`'s guard) before it runs.
- `///` XML-doc only on interface members (CI job `xmldoc-scope`). Inline comments ≤3 lines, always English (CI job `comment-length`).
- `kind create` deadline: budget for a cold node-image pull (1.35 GB measured) — do not reuse helm's 2-minute test deadline; use something in the 5–10 minute range with the measured 28s warm-cache case as the fast path.
- No `ManagedCli` for `kind` (deliberate, per ticket) — PATH-probe only, mirroring `ActRuntimeStatus`/`LocalCiRuntime._DetectActAsync`.
- `KIND_EXPERIMENTAL_PROVIDER` (docker vs podman): leave the ambient environment untouched — do not clear/pin it the way `HelmCommand._LockedEnvironment` does for helm's vars. The operator's own choice of container runtime must not be silently overridden.

---

## File Structure

New files (all under `plugins-dev/Cockpit.Plugin.Kubernetes/`):

- `Cli/CliCommand.cs` — generic process invocation shape (filename/argv/env/stdin), extracted from `Helm/HelmCommand.cs`'s implicit shape.
- `Cli/CliResult.cs` — generic process result (Started/TimedOut/ExitCode/Stdout/Stderr/Succeeded), extracted from `HelmResult` in `Helm/IHelmRunner.cs`.
- `Cli/CliRunner.cs` — the generic engine: argv-only process start, both pipes drained concurrently, deadline via linked `CancellationTokenSource`, kill-on-timeout. Body lifted from `Helm/HelmRunner.cs`.
- `Kind/KindClusterRecord.cs` — registry entry: `Name`, `OwnerPaneId`, `KubeconfigPath`, `CreatedAt`, `IsPinned`.
- `Kind/KindRuntimeStatus.cs` — mirrors `ActRuntimeStatus`: `IsInstalled`/`Version`/`Message`.
- `Kind/KindRuntime.cs` — PATH-probe + cache, mirrors `LocalCiRuntime._DetectActAsync` + its cache-only-success behaviour, built on `CliRunner`.
- `Kind/KindCommand.cs` — builds a `CliCommand` for `kind create cluster` / `kind delete cluster` / `kind get clusters`, mirrors `Helm/HelmCommand.Build`.
- `Kind/KindFailure.cs` — turns a failed `CliResult` into an agent-facing message, mirrors `Helm/HelmFailure.cs`.
- `Kind/KindClusterManager.cs` — the central class: registry read/write via `KubernetesSettings.KindClusters`, `CreateAsync`/`DeleteAsync`/`List`, `ReconcileAsync` (startup orphan sweep), `SweepExpiredAsync` (TTL backstop), `StopAllAsync` (Dispose teardown), `ISupervisedActivitySource` implementation.
- `Mcp/KubernetesMcpTools.Kind.cs` — the three MCP tools: `kind_create`, `kind_list`, `kind_delete`.

Modified files:

- `Helm/HelmRunner.cs` — internals delegate to `Cli/CliRunner`; public `IHelmRunner` contract (`HelmCommand`/`HelmResult`) unchanged, so `HelmRunnerTests.cs` and every Helm call site stay untouched.
- `Security/ClusterAccessGate.cs` — `_RequestAsync`'s `cluster: ClusterRegistration` parameter becomes `clusterLabel: string` (only `.Label` was ever used); add `AuthorizeKindLifecycleAsync(string operation, string scope, string? paneId)` for the pre-registration create/delete consent (no `ClusterRegistration` exists yet at create time).
- `Settings/KubernetesSettings.cs` — add `KindClusters` (list, same `Get<List<T>>`/`Set` idiom as `Clusters`) and `KindClusterMaxLifetime` (TTL setting, default 4 hours).
- `Mcp/KubernetesMcpTools.cs` — constructor gains a `KindClusterManager` parameter.
- `KubernetesPlugin.cs` — construct `KindClusterManager`, register it via `host.AddSupervisedActivityProvider`, call its startup reconcile against `host.Sessions.OpenSessions`, tear it down in `Dispose()` bounded/best-effort like the existing `_portForwards` teardown.
- `Ui/KubernetesSettingsControl.cs` — read-only kind-cluster list (name/owner/age/pinned checkbox) + the TTL setting, following the existing `ClusterRowControl`/`_Label`/`_Hint` idiom.
- `plugin.json` — version bump + description addition naming the kind-lifecycle and its grenzen (v1-scope: name only, no CI use, no multi-node).

New test files (all under `plugins-dev/Cockpit.Plugin.Kubernetes.Tests/`):

- `CliRunnerTests.cs` — same three cases as `HelmRunnerTests.cs` (not-started, exit-zero-with-stderr, locked/ambient env), proving the extraction preserved behaviour.
- `KindRuntimeTests.cs` — installed/not-installed/cache-only-on-success, mirrors the LocalCi runtime test shape.
- `KindCommandTests.cs` — argv shape for create/delete/list, mirrors `HelmCommandTests.cs`.
- `KindClusterManagerTests.cs` — the registry/sweep/TTL/pin unit tests (criteria 6, 8, 10, 11) using a fake `CliRunner`-level seam (an `IKindProcess`/injected `CliRunner` double) so no real `kind` binary is needed.
- `KindMcpToolsTests.cs` — consent-gate wiring + ownership-guard tests for the three tools, `ClusterAccessGateTests.cs`-style with `Substitute.For<ICockpitHost>()`.
- `KindClusterLiveTests.cs` — the real-binary, real-cluster test (criteria 1, 5, 14), gated exactly like `HelmUpgradeClusterTests.cs` (skip — `return`, not `Assert.Skip` — when `kind`/docker are unavailable, never Assert-fail on a missing local tool). Proves the plugin's own `kind_create` output is a valid `COCKPIT_HELM_KIND_KUBECONFIG` value (current-context starts with `kind-`), and that `get_resource` on `default` works right after create with zero manual steps.

---

## Task 1 — Extract the generic CLI runner

**Files:**
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes/Cli/CliCommand.cs`
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes/Cli/CliResult.cs`
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes/Cli/CliRunner.cs`
- Modify: `plugins-dev/Cockpit.Plugin.Kubernetes/Helm/HelmRunner.cs` (internals only — delegate to `CliRunner`)
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes.Tests/CliRunnerTests.cs`

**Pattern to copy:** `Helm/HelmRunner.cs` body verbatim, generalized: `HelmCommand` → `CliCommand` (same 4 fields: `FileName`, `Arguments`, `Environment`, `StandardInput`), `HelmResult` → `CliResult` (same shape: `Started`, `TimedOut`, `ExitCode`, `Stdout`, `Stderr`, factories `NotStarted`/`Timeout`/`Exited(...)`, computed `Succeeded`).

**Interfaces:**
- Produces: `internal sealed record CliCommand(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string,string> Environment, string? StandardInput = null)`.
- Produces: `internal sealed record CliResult(bool Started, bool TimedOut, int ExitCode, string Stdout, string Stderr)` with `static CliResult NotStarted`, `static CliResult Timeout`, `static CliResult Exited(int exitCode, string stdout, string stderr)`, `bool Succeeded => Started && !TimedOut && ExitCode == 0`.
- Produces: `internal sealed class CliRunner { public Task<CliResult> RunAsync(CliCommand command, TimeSpan timeout, CancellationToken cancellationToken = default); }`.
- `HelmRunner.RunAsync(HelmCommand, TimeSpan, CancellationToken) -> Task<HelmResult>` keeps its exact existing signature (still implements `IHelmRunner`); internally builds a `CliCommand` from the `HelmCommand`'s 4 fields, calls `CliRunner.RunAsync`, maps the returned `CliResult` back to `HelmResult` field-for-field.

**Tests:** `CliRunnerTests.cs` mirrors `HelmRunnerTests.cs`'s three cases against `CliRunner` directly. Then run the **existing, unmodified** `HelmRunnerTests.cs` and `HelmCommandTests.cs` — they must still pass unchanged, proving the delegation preserved `HelmRunner`'s external contract.

- [ ] Write `CliRunnerTests.cs` (adapted from `HelmRunnerTests.cs`) against the not-yet-existing `CliRunner`/`CliCommand`/`CliResult`
- [ ] Run it, confirm it fails to compile (types don't exist yet)
- [ ] Create `Cli/CliCommand.cs`, `Cli/CliResult.cs`, `Cli/CliRunner.cs`
- [ ] Run `CliRunnerTests.cs`, confirm it passes
- [ ] Refactor `Helm/HelmRunner.cs` to delegate to `CliRunner`
- [ ] Run `HelmRunnerTests.cs` + `HelmCommandTests.cs` unmodified, confirm still green
- [ ] Commit (see Git.md format — ticket line `AC-179`)

## Task 2 — Kind runtime detection

**Files:**
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes/Kind/KindRuntimeStatus.cs`
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes/Kind/KindRuntime.cs`
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes.Tests/KindRuntimeTests.cs`

**Pattern to copy:** `plugins-dev/Cockpit.Plugin.LocalCi/Runtime/ActRuntimeStatus.cs` (record shape + `Message` install-instructions) and `LocalCiRuntime._DetectActAsync`/`GetStatusAsync`'s cache-only-on-success + `SemaphoreSlim(1,1)` gate pattern — rebuilt on top of Task 1's `CliRunner` (`kind --version`, 5s `ProbeTimeout`, parse the last whitespace-token of stdout, same as `_ReadActVersion`).

**Interfaces:**
- Consumes: `CliRunner.RunAsync(CliCommand, TimeSpan, CancellationToken)` (Task 1).
- Produces: `internal sealed record KindRuntimeStatus(bool IsInstalled, string? Version) { static KindRuntimeStatus NotInstalled; string Message; }`.
- Produces: `internal sealed class KindRuntime(CliRunner runner) { Task<KindRuntimeStatus> DetectAsync(CancellationToken); }` — later consumed by `KindClusterManager.CreateAsync` (criterion 2) and by `KindCommand`'s callers to fail with an install message instead of a raw process-start error.

- [ ] Write `KindRuntimeTests.cs`: installed (fake runner returns exit 0 + `"kind v0.23.0 go1.22.1 linux/amd64"`), not-installed (fake runner returns `CliResult.NotStarted`), cache-only-on-success (two calls, second does not re-invoke the runner after a successful first)
- [ ] Run, confirm fails (types missing)
- [ ] Create `Kind/KindRuntimeStatus.cs`, `Kind/KindRuntime.cs`
- [ ] Run, confirm passes
- [ ] Commit

## Task 3 — Kind command builder + failure description

**Files:**
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes/Kind/KindCommand.cs`
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes/Kind/KindFailure.cs`
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes.Tests/KindCommandTests.cs`

**Pattern to copy:** `Helm/HelmCommand.cs` (argv assembly as a `List<string>`, never a shell string) and `Helm/HelmFailure.cs` (stderr string-match → best-effort guess, raw stderr tail always attached, `MaxStderrLength = 800`).

**Interfaces:**
- Produces: `internal static class KindCommand { static CliCommand Create(string kindExecutablePath, string name, string kubeconfigPath); static CliCommand Delete(string kindExecutablePath, string name, string kubeconfigPath); static CliCommand GetClusters(string kindExecutablePath); }` — no `(Command?, Error)` tuple needed here (unlike Helm) since there is no pasted-vs-path-kubeconfig branch to reject; a kind-managed cluster only ever has a path.
- Produces: `internal static class KindFailure { static string Describe(CliResult result, string kindExecutablePath); }` — stderr patterns to match: `"already exist"` (name collision), `"docker: command not found"` / `"Cannot connect to the Docker daemon"` (no container runtime), `"context deadline exceeded"` (timeout-adjacent apiserver-not-ready case), falling through to the generic guess.

- [ ] Write `KindCommandTests.cs`: assert exact argv for `Create`/`Delete`/`GetClusters` (`["create", "cluster", "--name", name, "--kubeconfig", path]` etc.)
- [ ] Run, confirm fails
- [ ] Create `Kind/KindCommand.cs`, `Kind/KindFailure.cs`
- [ ] Run, confirm passes
- [ ] Commit

## Task 4 — Registry model + settings persistence

**Files:**
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes/Kind/KindClusterRecord.cs`
- Modify: `plugins-dev/Cockpit.Plugin.Kubernetes/Settings/KubernetesSettings.cs`
- Modify: `plugins-dev/Cockpit.Plugin.Kubernetes.Tests/KubernetesSettingsTests.cs` (add cases)

**Pattern to copy:** `KubernetesSettings.Clusters` property (`storage.Get<List<T>>(key) ?? []` / `storage.Set(key, value.ToList())`) for `KindClusters`; the same file's `GetKubeconfig`/`SetKubeconfig` key-namespacing idiom is **not** needed here — a kind cluster's kubeconfig is a plain file path on disk (`KindClusterRecord.KubeconfigPath`), not a pasted secret, so it stays in the non-secret list itself (same reasoning as `ClusterRegistration.KubeconfigPath`).

**Interfaces:**
- Produces: `internal sealed record KindClusterRecord(string Name, string OwnerPaneId, string KubeconfigPath, DateTimeOffset CreatedAt, bool IsPinned = false);`
- Produces on `KubernetesSettings`: `IReadOnlyList<KindClusterRecord> KindClusters { get; set; }` and `TimeSpan KindClusterMaxLifetime { get; set; }` (backing key `kindClusterMaxLifetimeHours`, default 4.0 hours — the TTL backstop from criterion 11).
- Consumed by: `KindClusterManager` (Task 5).

- [ ] Write settings test cases: round-trip `KindClusters` through a `FakePluginStorage`, default `KindClusterMaxLifetime` is 4 hours, setting it persists
- [ ] Run, confirm fails
- [ ] Create `Kind/KindClusterRecord.cs`; add the two properties to `KubernetesSettings.cs`
- [ ] Run, confirm passes
- [ ] Commit

## Task 5 — KindClusterManager: create, delete, list

**Files:**
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes/Kind/KindClusterManager.cs`
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes.Tests/KindClusterManagerTests.cs`

**Pattern to copy:** `Cluster/PortForwardManager.cs` for the owning-collection shape (here: mutate `KubernetesSettings.KindClusters` instead of a `ConcurrentDictionary`, since this must survive a restart) and for the `ISupervisedActivitySource` implementation (`Label`, `Snapshot()`, `Changed` event fired after every mutation). `Model/ClusterRegistration.cs` for the record this class must also write into `KubernetesSettings.Clusters` on create (criterion 5): `Id = $"kind-{name}"`, `Label = name`, `ContextName = $"kind-{name}"`, `KubeconfigPath = <the file kind wrote>`, `AllowedNamespaces = ["default"]`.

**Interfaces:**
- Consumes: `KubernetesSettings` (Task 4), `KindRuntime` (Task 2), `KindCommand`/`KindFailure` (Task 3), `CliRunner` (Task 1), `ICockpitHost` (for state-root path + `Sessions.OpenSessions`, wired at construction from `KubernetesPlugin.Initialize`).
- Produces:
  ```
  internal sealed class KindClusterManager(KubernetesSettings settings, CliRunner runner, KindRuntime kindRuntime, string kindExecutablePath, string kubeconfigDirectory) : ISupervisedActivitySource
  {
      Task<(KindClusterRecord? Record, string? Error)> CreateAsync(string name, string ownerPaneId, CancellationToken cancellationToken);
      IReadOnlyList<KindClusterListEntry> List(); // Name, Age, OwnerPaneId, KubeconfigPath, IsPinned, IsRunning
      Task<(bool Ok, string? Error)> DeleteAsync(string name, CancellationToken cancellationToken);
      Task ReconcileAsync(IReadOnlyCollection<string> liveSessionIds, CancellationToken cancellationToken); // Task 6
      Task SweepExpiredAsync(CancellationToken cancellationToken); // Task 6
      Task StopAllAsync(CancellationToken cancellationToken); // Task 6
      string Label { get; } // ISupervisedActivitySource
      IReadOnlyList<SupervisedActivity> Snapshot();
      event Action? Changed;
  }
  ```
- `CreateAsync` flow: `kindRuntime.DetectAsync` → not installed: return the `KindRuntimeStatus.Message` as the error (criterion 2). Installed: build the kubeconfig target path under `kubeconfigDirectory` (a per-cluster file, never `~/.kube/config` — criterion 1), run `KindCommand.Create(...)` via `runner.RunAsync` with a 10-minute deadline (Global Constraints — cold pull), on failure return `KindFailure.Describe(...)`. On success: write `KindClusterRecord` into `settings.KindClusters`, write a matching `ClusterRegistration` into `settings.Clusters` (skip-and-report if a registration with that `Id` already exists and was hand-edited — criterion 6's "not silently overwritten" clause: compare against what `KindClusterManager` itself would have written last time; simplest correct check is "a `ClusterRegistration` with this `Id` already exists and this manager has no `KindClusterRecord` for this name" → report and stop, don't touch it), fire `Changed`.
- `DeleteAsync` flow: find the record; if absent, error (criterion 10 — never touch what is not registered). Guard the context name starts with `kind-` before calling `kind delete`. Run `KindCommand.Delete(...)`, on success remove the `KindClusterRecord`, remove the matching `ClusterRegistration`, delete the kubeconfig file, fire `Changed`.
- `List()` cross-checks `KindCommand.GetClusters` output for "is it still running" (criterion 3) without treating an empty `kind get clusters` as proof of absence on its own (Pitfall: only used for the `IsRunning` flag, never for what to sweep — the registry stays the source of truth for existence).

- [ ] Write `KindClusterManagerTests.cs` create/delete/list cases against a fake `CliRunner`-level double (inject a fake by making `CliRunner.RunAsync` virtual, or add an `ICliRunner` seam if `CliRunner` needs to be mocked — decide during implementation based on what `CliRunnerTests.cs` from Task 1 already established) and a `FakePluginStorage`-backed `KubernetesSettings`
- [ ] Run, confirm fails
- [ ] Implement `Kind/KindClusterManager.cs` (create/delete/list only — sweep/TTL/dispose land in Task 6)
- [ ] Run, confirm passes
- [ ] Commit

## Task 6 — Orphan sweep, TTL backstop, shutdown teardown

**Files:**
- Modify: `plugins-dev/Cockpit.Plugin.Kubernetes/Kind/KindClusterManager.cs` (add `ReconcileAsync`/`SweepExpiredAsync`/`StopAllAsync`)
- Modify: `plugins-dev/Cockpit.Plugin.Kubernetes.Tests/KindClusterManagerTests.cs` (add sweep cases)

**Pattern to copy:** `WorktreeManager.ReconcileAsync` body verbatim for the orphan logic: `records.Where(r => !liveSessionIds.Contains(r.OwnerPaneId) && !r.IsPinned)`, each deleted through the same `DeleteAsync` path (best-effort per-record try/catch so one bad orphan does not abort the sweep — mirrors the `_ReleaseOneAsync` try/catch in `WorktreeManager.ReconcileAsync`). `PortForwardManager.Start`'s `maxLifetime` + `_CloseAfterAsync` shape for `SweepExpiredAsync` (age check against `settings.KindClusterMaxLifetime`, skip pinned). `KubernetesPlugin.Dispose()`'s bounded `Wait(TimeSpan.FromSeconds(2))` + swallow-all pattern for `StopAllAsync`.

**Interfaces:**
- `Task ReconcileAsync(IReadOnlyCollection<string> liveSessionIds, CancellationToken cancellationToken)` — orphan = `record.OwnerPaneId` not in `liveSessionIds` **and** `!record.IsPinned`. Never age-based (criterion 8).
- `Task SweepExpiredAsync(CancellationToken cancellationToken)` — record age (`DateTimeOffset.UtcNow - record.CreatedAt`) exceeds `settings.KindClusterMaxLifetime` **and** `!record.IsPinned` → delete (criterion 11).
- `Task StopAllAsync(CancellationToken cancellationToken)` — delete every non-pinned record, best-effort, used from `KubernetesPlugin.Dispose()` (criterion 9).
- Safety valve test (criterion 10): feed `ReconcileAsync`/`SweepExpiredAsync` a live-set/clock that would sweep an unregistered name if the sweep ever iterated anything but `settings.KindClusters` — assert `kind delete` is never invoked for it.

- [ ] Write sweep test cases: dead-owner record → deleted; live-owner record → kept; pinned record with dead owner → kept (criterion 8); expired-but-pinned → kept, expired-and-unpinned → deleted (criterion 11); unregistered cluster name never touched by either sweep (criterion 10)
- [ ] Run, confirm fails
- [ ] Implement the three methods
- [ ] Run, confirm passes
- [ ] Commit

## Task 7 — Consent gate for kind lifecycle

**Files:**
- Modify: `plugins-dev/Cockpit.Plugin.Kubernetes/Security/ClusterAccessGate.cs`
- Modify: `plugins-dev/Cockpit.Plugin.Kubernetes.Tests/ClusterAccessGateTests.cs` (new cases only — existing cases must stay green, they only call public methods)

**Pattern to copy:** `ClusterAccessGate._AuthorizeMutationAsync`'s shape (`ConsentRisk.Dangerous`, `allowRemember: false`) — kind create/delete gets the same treatment since there is no `ClusterRegistration` yet to hang a namespace-jail/capability-opt-in check off of (unlike `AuthorizeDangerAsync`).

**Interfaces:**
- Modify: `_RequestAsync`'s `cluster: ClusterRegistration` parameter → `clusterLabel: string` (the only field ever read off `cluster` inside `_RequestAsync` was `.Label`, for the denial message). Update the 3 existing internal call sites to pass `cluster.Label` instead of `cluster`.
- Produces: `public Task<GateResult> AuthorizeKindLifecycleAsync(string operation, string scope, string? paneId) => _RequestAsync(title: "Kubernetes: kind cluster lifecycle", operation: operation, clusterLabel: "kind", scope: scope, risk: ConsentRisk.Dangerous, allowRemember: false, paneId: paneId);` — `scope` differs between create (`"k8s.kind.create"`) and delete (`$"k8s.kind.delete:{name}"`) so criterion 7 ("a create consent is not a delete consent") holds structurally.

- [ ] Write `ClusterAccessGateTests.cs` cases: `AuthorizeKindLifecycleAsync` asks with `ConsentRisk.Dangerous`/`allowRemember: false`, distinct scopes for create vs. delete, operation string carries the literal `kind` argv
- [ ] Run, confirm fails
- [ ] Implement the `_RequestAsync` signature change + `AuthorizeKindLifecycleAsync`
- [ ] Run full `ClusterAccessGateTests.cs` (existing + new), confirm all green
- [ ] Commit

## Task 8 — MCP tools: kind_create, kind_list, kind_delete

**Files:**
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes/Mcp/KubernetesMcpTools.Kind.cs`
- Modify: `plugins-dev/Cockpit.Plugin.Kubernetes/Mcp/KubernetesMcpTools.cs` (constructor gains `KindClusterManager kindClusters`)
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes.Tests/KindMcpToolsTests.cs`

**Pattern to copy:** the `PortForward` tool method in `Mcp/KubernetesMcpTools.cs` verbatim shape: `[McpServerTool(Name = "...")]` + `[Description("...")]` on method and every parameter, `session` as second parameter, gate call immediately after input validation with the `if (decision is { IsAllowed: false, DeniedReason: { } reason }) return McpText.Error(reason);` idiom, `Task<string>` return via `McpText.Ok(new {...})`/`McpText.Error(...)`. Ownership check on delete mirrors `WorktreeTools.RemoveAsync`'s `McpRequestContext`/`host.CurrentMcpCallerPaneId ?? session` pattern (via `DiagramMcpTools.ListDiagrams`'s exact `host.CurrentMcpCallerPaneId ?? session` line, since this class already has `host` injected).

**Interfaces:**
- Consumes: `KindClusterManager` (Task 5/6), `ClusterAccessGate.AuthorizeKindLifecycleAsync` (Task 7).
- Produces three MCP tools:
  - `kind_create(string session, string name) -> Task<string>` — validates `name` (DNS-label-safe, matches what `kind`/k8s context names accept), gate with operation string `$"kind create cluster --name {name} --kubeconfig <path>"` and scope `"k8s.kind.create"`, then `kindClusters.CreateAsync(name, host.CurrentMcpCallerPaneId ?? session, cancellationToken)`.
  - `kind_list() -> Task<string>` — no gate (read-only, mirrors `worktree_list`/`HelmList` being ungated reads); returns the `List()` entries as JSON.
  - `kind_delete(string session, string name) -> Task<string>` — gate with operation string `$"kind delete cluster --name {name}"` and scope `$"k8s.kind.delete:{name}"`, then `kindClusters.DeleteAsync(name, cancellationToken)`.

- [ ] Write `KindMcpToolsTests.cs`: create asks the gate with the right scope/operation and calls the manager on approval, denied consent short-circuits before the manager is touched, list returns JSON with the documented fields, delete same gate shape as create with its own scope
- [ ] Run, confirm fails
- [ ] Implement `Mcp/KubernetesMcpTools.Kind.cs`, thread `kindClusters` through the constructor
- [ ] Run, confirm passes
- [ ] Commit

## Task 9 — Plugin wiring: status bar, startup sweep, shutdown

**Files:**
- Modify: `plugins-dev/Cockpit.Plugin.Kubernetes/KubernetesPlugin.cs`

**Pattern to copy:** the existing `_portForwards` wiring in `Initialize`/`Dispose` verbatim shape — construct, `host.AddSupervisedActivityProvider(kindClusters)`, bounded/best-effort teardown in `Dispose()`.

**Interfaces:**
- `Initialize(ICockpitHost host)`: construct `KindClusterManager` (needs a per-plugin state directory for kubeconfig files — check what `host` exposes for a plugin state root; if nothing exists, use `Path.Combine(Path.GetTempPath(), "cockpit-kind", pluginId)` or equivalent, created on demand), `host.AddSupervisedActivityProvider(kindClusters)`, fire-and-forget `_ = kindClusters.ReconcileAsync(host.Sessions.OpenSessions.Select(s => s.PaneId).ToList())` at startup (mirrors `Program.cs`'s fire-and-forget reconcile call, but self-contained in the plugin per AC-885's layering rule — no `Cockpit.App`/`Cockpit.Core` change needed since `ICockpitHost.Sessions` already gives a live-session view).
- `Dispose()`: after the existing `_portForwards?.StopAllAsync().Wait(TimeSpan.FromSeconds(2))` block, add the same bounded/best-effort call for `_kindClusters?.StopAllAsync(...).Wait(TimeSpan.FromSeconds(2))` — separate try/catch so one hanging teardown does not block the other (criterion 9).

- [ ] Modify `KubernetesPlugin.cs`: field + construction + `AddSupervisedActivityProvider` + startup reconcile + `Dispose()` teardown
- [ ] Build the plugin project, confirm it compiles
- [ ] Commit

## Task 10 — Settings UI: kind-cluster list + TTL setting

**Files:**
- Modify: `plugins-dev/Cockpit.Plugin.Kubernetes/Ui/KubernetesSettingsControl.cs`

**Pattern to copy:** the existing `_clustersPanel`/`_Label`/`_Hint`/`addCluster` idiom in the same file — a read-only `StackPanel` of rows (name, owner, age, kubeconfig path) each with a `CheckBox` bound to `IsPinned`, plus a numeric field for `KindClusterMaxLifetime` (hours) next to the existing `_mcpEnabled` checkbox. Pin is operator-set (D2) — this view is the only place it can be toggled; there is no MCP tool for it.

**⚠️ Iron Law #9 note:** this is Avalonia UI code with no render/screenshot harness available in this environment. I cannot visually verify it — flagged explicitly rather than claimed working. Raymond should eyeball it live before merge.

- [ ] Add the kind-cluster section to `KubernetesSettingsControl` following the exact existing code-behind idiom (no new patterns introduced)
- [ ] Build, confirm it compiles
- [ ] Commit, note in the PR description that this view needs a live look

## Task 11 — plugin.json version bump + description

**Files:**
- Modify: `plugins-dev/Cockpit.Plugin.Kubernetes/plugin.json`
- Modify: `KubernetesPlugin.cs`'s `Metadata.Description` (kept in sync per the existing pattern — both already diverge slightly in length, matched at the "names the kind lifecycle" level)

- [ ] Bump `version` (minor — new capability, additive)
- [ ] Extend `description` to name the kind-lifecycle tools and their v1 grenzen (name-only, no CI use, non-pinned clusters torn down on close)
- [ ] Commit

## Task 12 — Live end-to-end test (criteria 1, 5, 14)

**Files:**
- Create: `plugins-dev/Cockpit.Plugin.Kubernetes.Tests/KindClusterLiveTests.cs`

**Pattern to copy:** `HelmUpgradeClusterTests.cs` verbatim shape — a guard method that returns early (`return`, no `Assert`) when `kind` is not on PATH, real `kind create`/`kind delete` against `cockpit-ac179-live` or similar, deadline sized per the Global Constraints note (not the helm test's 2 minutes).

**Interfaces:**
- Consumes: `KindClusterManager.CreateAsync`/`DeleteAsync` (Task 5/6), `ClusterConnectionFactory`/`get_resource` (existing) for criterion 5's "reachable with zero manual steps" proof.

**Test body:**
1. Guard: skip if `kind --version` fails (reuse `KindRuntime` or a bare probe).
2. `CreateAsync("cockpit-ac179-live", "test-owner", ...)`.
3. Assert: `~/.kube/config` unchanged (0 references — criterion 1's exact check from the ticket's own measurement).
4. Assert: the returned kubeconfig's `current-context` starts with `kind-` (criterion 14 — the exact guard `HelmUpgradeClusterTests._Kubeconfig()` uses).
5. Assert: `settings.Clusters` now contains a matching `ClusterRegistration`; use `ClusterConnectionFactory` to `get_resource` on `default` and confirm it succeeds (criterion 5).
6. `finally`: `DeleteAsync`, assert `kind get clusters` no longer lists it and the kubeconfig file is gone (criterion 4).

- [ ] Write the test per the body above
- [ ] Run it locally (requires `kind` + docker on this machine — confirmed present per the ticket's own measurement) — this is the one test in this plan that actually exercises a real cluster; run it deliberately, once, and watch it clean up
- [ ] Confirm pass, confirm `docker ps`/`kind get clusters` show 0 residue after the run
- [ ] Commit

## Task 13 — Full suite, rebase, local CI, PR

- [ ] `git fetch origin && git rebase origin/main`
- [ ] Re-run the full `Cockpit.Plugin.Kubernetes.Tests` project (`dotnet test`, not `run_local_checks`-only) against the rebased tree
- [ ] `run_local_checks`; if it aborts without a verdict, fall back to `dotnet test` directly rather than reading the abort as green
- [ ] Push, open PR (title references AC-179), do not self-merge
- [ ] Notify `cockpit-assistant` with the PR number

---

## Self-Review

**Spec coverage** (criterion → task):
1. own kubeconfig file, never `~/.kube/config` → Task 5, proven live in Task 12
2. missing-binary detection → Task 2
3. `kind_list` fields incl. running-check → Task 5 (`List()`), Task 8 (tool)
4. `kind_delete` full teardown → Task 5, proven live in Task 12
5. auto-registration into `ClusterRegistration` → Task 5, proven live in Task 12
6. delete removes the registration, no silent overwrite on name collision → Task 5
7. consent, create ≠ delete → Task 7
8. startup orphan sweep, pin exempt → Task 6
9. shutdown teardown, pin exempt → Task 6, Task 9
10. safety valve (only registry-known clusters touched) → Task 6 (test), design note in Task 5
11. TTL backstop → Task 4 (setting), Task 6 (enforcement)
12. status bar visibility + Kill → Task 5 (`ISupervisedActivitySource`), Task 9 (registration)
13. conventions (xmldoc scope, comment length, plugin.json bump) → Task 11, held to throughout every task
14. `HelmUpgradeClusterTests` guard compatibility → Task 12

**Placeholder scan:** no task leaves a "TBD"/"add error handling" gap; every method got a described flow. The one open implementation choice (whether `CliRunner` needs virtual methods or an `ICliRunner` seam for `KindClusterManagerTests.cs` mocking) is flagged explicitly in Task 5 as a decision to make during that task, not a placeholder in the design — Task 1 already answers it in practice once `CliRunnerTests.cs` exists.

**Type consistency:** `CliCommand`/`CliResult`/`CliRunner` (Task 1) are the types every later task's "Consumes" line names; `KindClusterRecord` (Task 4) is what Tasks 5/6/8 all operate on; `KindClusterManager`'s public surface (Task 5/6) is exactly what Task 8's tools and Task 9's plugin wiring call — no renamed methods between tasks.
