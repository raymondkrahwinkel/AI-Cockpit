namespace Cockpit.Plugin.Browser.Ipc;

internal static class BrowserIpcCleanup
{
    internal static void Try(Action cleanup, List<Exception> errors)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }
}
