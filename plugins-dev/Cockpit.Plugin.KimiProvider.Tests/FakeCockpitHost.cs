using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider.Tests;

/// <summary>
/// A minimal <see cref="ICockpitHost"/> test double for <see cref="KimiProviderPluginTests"/> — captures the
/// <see cref="SessionProviderRegistration"/> <see cref="ICockpitPlugin.Initialize"/> passes to
/// <see cref="AddSessionProvider"/>. Hand-written rather than an NSubstitute proxy: proxying the full interface
/// forces Castle to reflect over every member (including ones this test never touches), which drags in
/// unrelated host-only assemblies (Material.Icons, via <c>WidgetRegistration</c>) that a standalone plugin test
/// project has no reason to reference. Every other member keeps the interface's own default implementation.
/// </summary>
internal sealed class FakeCockpitHost : ICockpitHost
{
    public SessionProviderRegistration? CapturedRegistration { get; private set; }

    public IServiceProvider Services => throw new NotSupportedException("Not needed by KimiProviderPluginTests.");

    public ICockpitActions Actions => throw new NotSupportedException("Not needed by KimiProviderPluginTests.");

    public IPluginStorage Storage => throw new NotSupportedException("Not needed by KimiProviderPluginTests.");

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
