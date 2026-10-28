using Cockpit.Core.Layout;

namespace Cockpit.Infrastructure.Layout;

// AC-1827: the saved main-window bounds read before the container exists; null when absent or unreadable.
public static class StartupWindowBounds
{
    public static WindowBounds? ReadMain()
    {
        try
        {
            return new WindowBoundsStore().LoadAsync("main").GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
