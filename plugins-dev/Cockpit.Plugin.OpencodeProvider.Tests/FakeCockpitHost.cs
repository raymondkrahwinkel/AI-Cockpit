using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider.Tests;

// A minimal `ICockpitHost` test double for `OpencodeProviderPluginTests` — captures the
// `SessionProviderRegistration` `ICockpitPlugin.Initialize` passes to `AddSessionProvider`. Hand-written
// rather than an NSubstitute proxy: proxying the full interface forces Castle to reflect over every member
// (including ones this test never touches), which drags in unrelated host-only assemblies a standalone
// plugin test project has no reason to reference. Mirrors Cockpit.Plugin.KimiProvider.Tests.FakeCockpitHost.
internal sealed class FakeCockpitHost : ICockpitHost
{
    public SessionProviderRegistration? CapturedRegistration { get; private set; }

    public IServiceProvider Services => throw new NotSupportedException("Not needed by OpencodeProviderPluginTests.");

    public ICockpitActions Actions => throw new NotSupportedException("Not needed by OpencodeProviderPluginTests.");

    public IPluginStorage Storage => throw new NotSupportedException("Not needed by OpencodeProviderPluginTests.");

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

    public void AddSessionProvider(SessionProviderRegistration registration) => CapturedRegistration = registration;
}
