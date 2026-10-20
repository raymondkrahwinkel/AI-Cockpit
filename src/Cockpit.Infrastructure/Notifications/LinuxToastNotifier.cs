using System.Diagnostics;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Configuration;
using Cockpit.Core.Notifications;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Notifications;

// A desktop notification on Linux (#76), through `notify-send` — the one interface every desktop here actually
// implements, without adding a D-Bus dependency. Previously this platform got `NoOpToastNotifier`, so the
// "present" half of the router delivered nothing here; a failed delivery is logged and dropped rather than thrown.
internal sealed class LinuxToastNotifier(ILogger<LinuxToastNotifier> logger) : IToastNotifier
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    // How long a clickable notification keeps notify-send waiting for the click (AC-1588).
    private static readonly TimeSpan ClickWait = TimeSpan.FromMinutes(10);

    // AC-1588: silent, with a default action; notify-send --wait prints the action's name when it is clicked. An
    // older notify-send without --action fails, and then the plain notification is shown without a click.
    public async Task<bool> NotifySilentlyAsync(AttentionNotification notification, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "notify-send",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add($"--app-name={CockpitProduct.DisplayName}");
        startInfo.ArgumentList.Add("--urgency=normal");
        startInfo.ArgumentList.Add("--hint=boolean:suppress-sound:true");
        startInfo.ArgumentList.Add("--action=default=Open");
        startInfo.ArgumentList.Add("--wait");
        startInfo.ArgumentList.Add(notification.Title);
        startInfo.ArgumentList.Add(notification.Body);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                logger.LogDebug("notify-send did not start; the notification was dropped.");
                return false;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ClickWait);
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode != 0)
                {
                    await NotifyAsync(notification, cancellationToken);
                    return false;
                }

                return string.Equals(output.Trim(), "default", StringComparison.Ordinal);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Our own child, by its own handle: nobody clicked within ClickWait.
                process.Kill();
                return false;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "A desktop notification could not be delivered.");
            return false;
        }
    }

    public async Task NotifyAsync(AttentionNotification notification, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "notify-send",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add($"--app-name={CockpitProduct.DisplayName}");

        // Normal, not critical: critical notifications on most desktops never expire, and a cockpit that leaves a
        // stack of undismissable banners behind is one you turn notifications off for.
        startInfo.ArgumentList.Add("--urgency=normal");
        startInfo.ArgumentList.Add(notification.Title);
        startInfo.ArgumentList.Add(notification.Body);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                logger.LogDebug("notify-send did not start; the notification was dropped.");
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Patience);

            await process.WaitForExitAsync(timeout.Token);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                logger.LogDebug("notify-send exited with {Code}: {Error}", process.ExitCode, error.Trim());
            }
        }
        catch (Exception exception)
        {
            // No notify-send installed, no notification daemon running, a desktop that refuses: none of it is worth
            // taking the cockpit down for.
            logger.LogDebug(exception, "A desktop notification could not be delivered.");
        }
    }
}
