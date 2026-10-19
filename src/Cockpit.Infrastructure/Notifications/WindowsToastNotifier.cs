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
    // How long a clickable toast keeps PowerShell waiting for the click before it gives up.
    private static readonly TimeSpan ClickWait = TimeSpan.FromMinutes(10);

    private const string PowerShellAppId = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";

    public async Task NotifyAsync(AttentionNotification notification, CancellationToken cancellationToken = default)
    {
        var script = BuildToastScript(notification);
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

    // AC-1588: silent, and PowerShell stays to hear the click; it prints "activated" when the toast was clicked.
    // ponytail: a click after PowerShell gave up (ClickWait) is lost; a registered AppUserModelID would keep it.
    public async Task<bool> NotifySilentlyAsync(AttentionNotification notification, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command -",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                logger.LogWarning("Could not start PowerShell to raise a toast for '{Title}'.", notification.Title);
                return false;
            }

            await process.StandardInput.WriteAsync(BuildSilentClickableToastScript(notification)).ConfigureAwait(false);
            process.StandardInput.Close();
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

    internal static string BuildSilentClickableToastScript(AttentionNotification notification)
    {
        var title = EscapeForXml(notification.Title);
        var body = EscapeForXml(notification.Body);
        var waitSeconds = (int)ClickWait.TotalSeconds;

        return $$"""
            $ErrorActionPreference = 'Stop'
            [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null
            [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime] | Out-Null
            $xml = @"
            <toast><visual><binding template="ToastGeneric"><text>{{title}}</text><text>{{body}}</text></binding></visual><audio silent="true"/></toast>
            "@
            $doc = New-Object Windows.Data.Xml.Dom.XmlDocument
            $doc.LoadXml($xml)
            $toast = New-Object Windows.UI.Notifications.ToastNotification $doc
            Register-ObjectEvent -InputObject $toast -EventName Activated -SourceIdentifier CockpitToastActivated | Out-Null
            Register-ObjectEvent -InputObject $toast -EventName Dismissed -SourceIdentifier CockpitToastDismissed | Out-Null
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{{PowerShellAppId}}').Show($toast)
            $event = Wait-Event -Timeout {{waitSeconds}}
            if ($event -and $event.SourceIdentifier -eq 'CockpitToastActivated') { 'activated' }
            """;
    }

    private static string BuildToastScript(AttentionNotification notification)
    {
        var title = EscapeForXml(notification.Title);
        var body = EscapeForXml(notification.Body);

        return $"""
            $ErrorActionPreference = 'Stop'
            [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null
            [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime] | Out-Null
            $xml = @"
            <toast><visual><binding template="ToastGeneric"><text>{title}</text><text>{body}</text></binding></visual></toast>
            "@
            $doc = New-Object Windows.Data.Xml.Dom.XmlDocument
            $doc.LoadXml($xml)
            $toast = New-Object Windows.UI.Notifications.ToastNotification $doc
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{PowerShellAppId}').Show($toast)
            """;
    }

    private static string EscapeForXml(string value) => SecurityElement.Escape(value) ?? value;
}
