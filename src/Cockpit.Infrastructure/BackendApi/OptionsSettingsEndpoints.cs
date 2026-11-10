using System.Text.Json.Serialization;
using Cockpit.Core.Abstractions.Clones;
using Cockpit.Core.Abstractions.Debugging;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.HeavyRuns;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.BackendApi;

internal static class OptionsSettingsEndpoints
{
    internal static SemaphoreSlim EditGate { get; } = new(1, 1);

    public static void Map(RouteGroupBuilder api, IServiceProvider services)
    {
        IHeavyRunSettingsStore runs() => services.GetRequiredService<IHeavyRunSettingsStore>();
        ISessionBehaviorSettingsStore behavior() => services.GetRequiredService<ISessionBehaviorSettingsStore>();
        ICloneSettingsStore clones() => services.GetRequiredService<ICloneSettingsStore>();
        IWorktreeSettingsStore worktrees() => services.GetRequiredService<IWorktreeSettingsStore>();
        INotificationSettingsStore notifications() => services.GetRequiredService<INotificationSettingsStore>();
        IDebugSettingsStore debug() => services.GetRequiredService<IDebugSettingsStore>();
        ITerminalSettingsStore terminal() => services.GetRequiredService<ITerminalSettingsStore>();

        _Map<RunOptions>("runs", async ct =>
        {
            var value = await runs().LoadAsync(ct).ConfigureAwait(false);
            return new(value.MaxParallelRuns, value.CpuCeilingPercent, value.MemoryCeilingPercent);
        }, async (value, ct) =>
        {
            var stored = await runs().LoadAsync(ct).ConfigureAwait(false);
            await runs().SaveAsync(stored with { MaxParallelRuns = value.MaxParallelRuns, CpuCeilingPercent = value.CpuCeilingPercent, MemoryCeilingPercent = value.MemoryCeilingPercent }, ct).ConfigureAwait(false);
            await services.GetRequiredService<IHeavyRunSettingsRefresh>().RefreshSettingsAsync().ConfigureAwait(false);
        }, value => value.MaxParallelRuns >= 0 && value.CpuCeilingPercent is > 0 and <= 100 && value.MemoryCeilingPercent is > 0 and <= 100);

        _Map<SessionOptions>("sessions", async ct =>
        {
            var value = await behavior().LoadAsync(ct).ConfigureAwait(false);
            return new(value.AutoCloseOnExit, value.CombineQueuedMessages, value.WakeAgentsByDefault, value.MemoryBudgetPercent, value.PluginRestartAdviceAfter);
        }, async (value, ct) =>
        {
            var stored = await behavior().LoadAsync(ct).ConfigureAwait(false);
            await behavior().SaveAsync(stored with { AutoCloseOnExit = value.AutoCloseOnExit, CombineQueuedMessages = value.CombineQueuedMessages, WakeAgentsByDefault = value.WakeAgentsByDefault, MemoryBudgetPercent = value.MemoryBudgetPercent, PluginRestartAdviceAfter = value.PluginRestartAdviceAfter }, ct).ConfigureAwait(false);
            services.GetRequiredService<Cockpit.Core.Abstractions.Agents.IWorkspaceAgentCoordinator>().SetDefaultWakeConsent(value.WakeAgentsByDefault);
        }, value => value.MemoryBudgetPercent is > 0 and <= 100 && value.PluginRestartAdviceAfter >= 0);

        _Map<RootOptions>("clones", async ct => new((await clones().LoadAsync(ct).ConfigureAwait(false)).Root, clones().DefaultRoot),
            (value, ct) => clones().SaveAsync(new() { Root = value.Root }, ct), value => _ValidRoot(value.Root));
        _Map<RootOptions>("worktrees", async ct => new((await worktrees().LoadAsync(ct).ConfigureAwait(false)).Root, worktrees().DefaultRoot),
            (value, ct) => worktrees().SaveAsync(new() { Root = value.Root }, ct), value => _ValidRoot(value.Root));

        _Map<NotificationOptions>("notifications", async ct =>
        {
            var value = await notifications().LoadAsync(ct).ConfigureAwait(false);
            return new(value.DiscordEnabled, value.HasWebhookUrl ? ProfileEndpoints.KeptSecret : null, value.IdleThreshold, value.SessionIdleThreshold, value.NotifyOnSessionFinished, value.NotifyOnSessionIdle, value.NotifyWhenAllSessionsIdle, value.NotifyOnCiFailure, value.MonitorMessagesPerHour);
        }, async (value, ct) =>
        {
            var stored = await notifications().LoadAsync(ct).ConfigureAwait(false);
            await notifications().SaveAsync(stored with
            {
                DiscordEnabled = value.DiscordEnabled,
                WebhookUrl = value.WebhookUrl == ProfileEndpoints.KeptSecret ? stored.WebhookUrl : value.WebhookUrl,
                IdleThreshold = value.IdleThreshold,
                SessionIdleThreshold = value.SessionIdleThreshold,
                NotifyOnSessionFinished = value.NotifyOnSessionFinished,
                NotifyOnSessionIdle = value.NotifyOnSessionIdle,
                NotifyWhenAllSessionsIdle = value.NotifyWhenAllSessionsIdle,
                NotifyOnCiFailure = value.NotifyOnCiFailure,
                MonitorMessagesPerHour = value.MonitorMessagesPerHour,
            }, ct).ConfigureAwait(false);
        }, value => value.IdleThreshold > TimeSpan.Zero && value.SessionIdleThreshold >= TimeSpan.Zero && value.MonitorMessagesPerHour > 0
            && (string.IsNullOrEmpty(value.WebhookUrl) || value.WebhookUrl == ProfileEndpoints.KeptSecret
                || Uri.TryCreate(value.WebhookUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps));

        _Map<DiagnosticOptions>("diagnostics", async ct => new((await debug().LoadAsync(ct).ConfigureAwait(false)).LogDiagnosticSnapshots),
            async (value, ct) => await debug().SaveAsync((await debug().LoadAsync(ct).ConfigureAwait(false)) with { LogDiagnosticSnapshots = value.LogDiagnosticSnapshots }, ct).ConfigureAwait(false), _ => true);
        _Map<ShellOptions>("shell", async ct => new((await terminal().LoadAsync(ct).ConfigureAwait(false)).Shell, Cockpit.Core.Terminal.ShellCatalog.Detect()),
            async (value, ct) => await terminal().SaveAsync((await terminal().LoadAsync(ct).ConfigureAwait(false)) with { Shell = value.Shell }, ct).ConfigureAwait(false),
            value => value.Shell == string.Empty || Cockpit.Core.Terminal.ShellCatalog.Detect().Any(shell =>
                string.Equals(value.Shell, shell.Id, StringComparison.Ordinal)
                || string.Equals(value.Shell, shell.ExecutablePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)));

        // Fixed typed routes, never a config-file or service-name dispatcher.
        void _Map<T>(string group, Func<CancellationToken, Task<T>> read, Func<T, CancellationToken, Task> save, Func<T, bool> valid) where T : class
        {
            var route = $"/options/{group}";
            api.MapGet(route, async (CancellationToken ct) => Results.Json(await read(ct).ConfigureAwait(false), ConnectKeyEndpoints.Json)).RequireAdmin();
            api.MapPut(route, async (HttpRequest request, CancellationToken ct) =>
            {
                var (body, refused) = await ProfileEndpoints.ReadBodyAsync<T>(request, "The settings group contains an unknown field.", ct, root => root, "webhookUrl").ConfigureAwait(false);
                if (body is null)
                {
                    return refused;
                }

                if (!valid(body))
                {
                    return BackendApiRoutes.Error(StatusCodes.Status400BadRequest, "invalid_request", $"The {group} settings contain an invalid value.");
                }

                await EditGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await save(body, ct).ConfigureAwait(false);
                    return Results.Json(await read(ct).ConfigureAwait(false), ConnectKeyEndpoints.Json);
                }
                finally
                {
                    EditGate.Release();
                }
            }).RequireAdmin().Audited($"options_{group}", services);
        }
    }

    private static bool _ValidRoot(string? root) => string.IsNullOrWhiteSpace(root) || (!root.Contains('\0') && Path.IsPathFullyQualified(root));
}

internal sealed record RunOptions([property: JsonRequired] int MaxParallelRuns, [property: JsonRequired] double CpuCeilingPercent, [property: JsonRequired] double MemoryCeilingPercent);
internal sealed record SessionOptions([property: JsonRequired] bool AutoCloseOnExit, [property: JsonRequired] bool CombineQueuedMessages, [property: JsonRequired] bool WakeAgentsByDefault, [property: JsonRequired] int MemoryBudgetPercent, [property: JsonRequired] int PluginRestartAdviceAfter);
internal sealed record RootOptions([property: JsonRequired] string? Root, string? DefaultRoot = null);
internal sealed record NotificationOptions([property: JsonRequired] bool DiscordEnabled, [property: JsonRequired] string? WebhookUrl, [property: JsonRequired] TimeSpan IdleThreshold, [property: JsonRequired] TimeSpan SessionIdleThreshold, [property: JsonRequired] bool NotifyOnSessionFinished, [property: JsonRequired] bool NotifyOnSessionIdle, [property: JsonRequired] bool NotifyWhenAllSessionsIdle, [property: JsonRequired] bool NotifyOnCiFailure, [property: JsonRequired] int MonitorMessagesPerHour);
internal sealed record DiagnosticOptions([property: JsonRequired] bool LogDiagnosticSnapshots);
internal sealed record ShellOptions([property: JsonRequired] string Shell, IReadOnlyList<Cockpit.Core.Terminal.ShellDescriptor>? Shells = null);
