using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

// AC-1588: Cockpit.Notify <title> <body>. Shows the toast without a sound under PowerShell's app id (the cockpit is
// unpackaged and has none of its own) and prints "activated" when it is clicked. A banner that times out has only
// moved to the Action Center, so that is not the end; the operator closing it, or ClickWait running out, is.
const string PowerShellAppId = "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe";
var clickWait = TimeSpan.FromMinutes(10);

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Cockpit.Notify <title> <body>");
    return 2;
}

var document = new XmlDocument();
document.LoadXml(
    "<toast><visual><binding template=\"ToastGeneric\">"
    + $"<text>{SecurityElement.Escape(args[0])}</text><text>{SecurityElement.Escape(args[1])}</text>"
    + "</binding></visual><audio silent=\"true\"/></toast>");

var toast = new ToastNotification(document);
var outcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
toast.Activated += (_, _) => outcome.TrySetResult("activated");
toast.Failed += (_, _) => outcome.TrySetResult("failed");
toast.Dismissed += (_, dismissed) =>
{
    if (dismissed.Reason != ToastDismissalReason.TimedOut)
    {
        outcome.TrySetResult("dismissed");
    }
};

ToastNotificationManager.CreateToastNotifier(PowerShellAppId).Show(toast);
var finished = await Task.WhenAny(outcome.Task, Task.Delay(clickWait));
Console.WriteLine(finished == outcome.Task ? outcome.Task.Result : "none");
return 0;
