using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Discord.Tests;

// Only what `DiscordChannelSettingsControl`'s constructor takes from the host: its storage, and the AC-1033 `?`
// help hint, which is a default-interface member the control calls but this fake never needs to answer for.
// Everything else on `ICockpitHost` has a default the view never reaches.
internal sealed class FakeCockpitHost : ICockpitHost
{
    public IServiceProvider Services => throw new NotSupportedException("No test reaches the service provider.");

    public ICockpitActions Actions => throw new NotSupportedException("No test reaches the cockpit actions.");

    public IPluginStorage Storage { get; } = new FakePluginStorage();

    public void AddSettings(Func<Control> createView)
    {
    }

    public void AddSideMenuButton(string title, Action onInvoke)
    {
    }

    public void AddSideMenuSection(string title, Func<Control> createView)
    {
    }

    public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
        Task.CompletedTask;
}
