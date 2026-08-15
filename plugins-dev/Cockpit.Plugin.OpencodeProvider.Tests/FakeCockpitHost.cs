using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider.Tests;

// A minimal `ICockpitHost` test double that captures the registration `Initialize` passes to
// `AddSessionProvider`. Hand-written rather than NSubstitute — a full-interface proxy drags in host-only
// assemblies this standalone test project has no reason to reference. Mirrors Kimi's own FakeCockpitHost.
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
