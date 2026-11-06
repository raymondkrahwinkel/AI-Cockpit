namespace Cockpit.Plugin.Browser.UI.Engine
{
    // The probe uses no product lifecycle or permission state.
    internal sealed class BrowserEngine
    {
        public void Register(CefBrowserView view) { }
        public void Unregister(CefBrowserView view) { }
        public void Closing(Task task) { }
    }
}

namespace Cockpit.Plugin.Browser.UI.Share
{
    internal static class ControlPolicy
    {
        internal enum Verdict { Allow, Hold, Drop }

        public static Verdict Navigation(string? origin, string method, bool formSubmit, string url, bool mainFrame, bool fromHost)
        {
            if (origin is not null)
            {
                throw new InvalidOperationException("Permission tests are outside this transport probe.");
            }
            return Verdict.Allow;
        }
    }
}
