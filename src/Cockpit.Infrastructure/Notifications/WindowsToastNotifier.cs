using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;

namespace Cockpit.Infrastructure.Notifications;

// Best-effort Windows toast raised through PowerShell's WinRT `ToastNotificationManager`, since the
// cockpit is an unpackaged Win32 app with no AppUserModelID. Shows under "Windows PowerShell", not
// the cockpit's own identity; secondary to the verified Discord webhook path.
[SupportedOSPlatform("windows")]
internal sealed class WindowsToastNotifier(ILogger<WindowsToastNotifier> logger) : IToastNotifier
{
    private const string PowerShellAppId = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";

    // AC-1588: the helper beside Cockpit.App.exe that shows a silent toast and prints "activated" when it is clicked.
    private static readonly string NotifyHelperPath = Path.Combine(AppContext.BaseDirectory, "Cockpit.Notify.exe");

    public Task NotifyAsync(AttentionNotification notification, CancellationToken cancellationToken = default) =>
        _RunPowerShellToastAsync(notification, silent: false, cancellationToken);

    // AC-1588: PowerShell 5.1 cannot subscribe to a WinRT event, so the click is heard by Cockpit.Notify.exe. Without
    // it (a build that did not ship it) the toast is still shown, silently, and the click is not reported.
    public async Task<bool> NotifySilentlyAsync(AttentionNotification notification, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(NotifyHelperPath))
        {
            logger.LogWarning("{Helper} is missing, so a plugin notification is shown without reporting its click.", NotifyHelperPath);
            await _RunPowerShellToastAsync(notification, silent: true, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = NotifyHelperPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(notification.Title);
        startInfo.ArgumentList.Add(notification.Body);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                logger.LogWarning("Could not start {Helper} to raise a toast for '{Title}'.", NotifyHelperPath, notification.Title);
                return false;
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return output.Contains("activated", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Raising a Windows toast for '{Title}' failed.", notification.Title);
            return false;
        }
    }

    private async Task _RunPowerShellToastAsync(AttentionNotification notification, bool silent, CancellationToken cancellationToken)
    {
        var script = BuildToastScript(notification, silent);
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command -",
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                logger.LogWarning("Could not start PowerShell to raise a toast for '{Title}'.", notification.Title);
                return;
            }

            await process.StandardInput.WriteAsync(script).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Raising a Windows toast for '{Title}' failed.", notification.Title);
        }
    }

    private static string BuildToastScript(AttentionNotification notification, bool silent)
    {
        var title = EscapeForXml(notification.Title);
        var body = EscapeForXml(notification.Body);
        var audio = silent ? "<audio silent=\"true\"/>" : "";

        return $"""
            $ErrorActionPreference = 'Stop'
            [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null
            [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime] | Out-Null
            $xml = @"
            <toast><visual><binding template="ToastGeneric"><text>{title}</text><text>{body}</text></binding></visual>{audio}</toast>
            "@
            $doc = New-Object Windows.Data.Xml.Dom.XmlDocument
            $doc.LoadXml($xml)
            $toast = New-Object Windows.UI.Notifications.ToastNotification $doc
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{PowerShellAppId}').Show($toast)
            """;
    }

    private static string EscapeForXml(string value) => SecurityElement.Escape(value) ?? value;
}
