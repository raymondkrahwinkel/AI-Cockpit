using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram.Tests;

// ICockpitHost has no window members any more; a test host that also stands for the window's dialog host is a
// substitute of both, and the extension lets its dialogs be arranged and asserted as they always were.
public interface IWindowProbe
{
    Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width, double height);
}

public static class WindowProbeExtensions
{
    public static Task ShowDialogAsync(this ICockpitHost host, string title, Func<Control> createContent, string singleInstanceKey, double width, double height) =>
        ((IWindowProbe)host).ShowDialogAsync(title, createContent, singleInstanceKey, width, height);
}
