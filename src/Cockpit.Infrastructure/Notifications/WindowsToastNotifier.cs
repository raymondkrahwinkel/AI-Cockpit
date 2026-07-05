using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;

namespace Cockpit.Infrastructure.Notifications;

/// <summary>
/// Best-effort Windows toast for the present operator, raised through PowerShell's built-in WinRT
/// <c>ToastNotificationManager</c>. This route was chosen because the cockpit is an unpackaged Win32
/// app with no package identity: a real toast normally needs a registered AppUserModelID + Start-menu
/// shortcut, whereas PowerShell already ships one, so a genuine toast appears without pulling in a
/// <c>net*-windows10</c> toast package or a Windows-only target framework (Infrastructure stays
/// cross-platform <c>net10.0</c>).
/// </summary>
/// <remarks>
/// LIVE-CHECK FLAG (Iron Law #9): whether the toast actually renders on Raymond's desktop cannot be
/// observed from this sandbox, and the toast shows under the "Windows PowerShell" app identity rather
/// than the cockpit's own. It is a best-effort, lighter notification — not a faked one. The Discord
/// webhook branch is the primary, verified path; this toast path is the secondary one.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsToastNotifier(ILogger<WindowsToastNotifier> logger) : IToastNotifier
{
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
