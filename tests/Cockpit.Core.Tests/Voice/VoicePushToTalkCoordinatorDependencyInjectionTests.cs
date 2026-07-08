using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core;
using Cockpit.Infrastructure;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// Guards the #34 DI wiring end-to-end: building the real container the way <c>Program.cs</c> does
/// must resolve <see cref="VoicePushToTalkCoordinator"/> and its dependencies (the overlay view model,
/// the overlay presenter, the platform hotkey service) without touching Avalonia's windowing — the
/// coordinator's constructor and <see cref="VoiceOverlayPresenter"/>'s must not eagerly create the
/// <c>VoiceOverlayWindow</c>, since Avalonia is not initialized in this plain-container test host.
/// </summary>
public class VoicePushToTalkCoordinatorDependencyInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);

        services.AddTransient<Func<ClaudeSessionViewModel>>(
            provider => () => provider.GetRequiredService<ClaudeSessionViewModel>());
        services.AddTransient<Func<ClaudeTtyViewModel>>(
            provider => () => provider.GetRequiredService<ClaudeTtyViewModel>());

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Container_ResolvesTheCoordinatorAndItsDependencies()
    {
        await using var provider = BuildProvider();

        provider.GetService<VoicePushToTalkCoordinator>().Should().NotBeNull();
        provider.GetService<VoiceOverlayViewModel>().Should().NotBeNull();
        provider.GetService<IVoiceOverlayPresenter>().Should().BeOfType<VoiceOverlayPresenter>();
    }

    [Fact]
    public async Task Container_ResolvesTheSameOverlayViewModelInstance_ForTheCoordinatorAndThePresenter()
    {
        await using var provider = BuildProvider();

        var coordinator = provider.GetRequiredService<VoicePushToTalkCoordinator>();
        var overlay = provider.GetRequiredService<VoiceOverlayViewModel>();

        coordinator.Overlay.Should().BeSameAs(overlay);
    }
}
