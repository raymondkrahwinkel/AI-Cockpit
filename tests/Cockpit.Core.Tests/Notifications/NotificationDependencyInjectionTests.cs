using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Infrastructure;
using FluentAssertions;

namespace Cockpit.Core.Tests.Notifications;

/// <summary>
/// Guards the notification DI wiring end-to-end: building the real container the way
/// <c>Program.cs</c> does must resolve the presence/notification services and, transitively, the
/// <see cref="CockpitViewModel"/> that depends on them — a missing registration would break app start.
/// </summary>
public class NotificationDependencyInjectionTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Cockpit.Core.DependencyInjection).Assembly,
            typeof(Cockpit.Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);

        services.AddTransient<Func<SessionViewModel>>(
            provider => () => provider.GetRequiredService<SessionViewModel>());
        services.AddTransient<Func<ClaudeTtyViewModel>>(
            provider => () => provider.GetRequiredService<ClaudeTtyViewModel>());

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Container_ResolvesTheAttentionNotifierAndItsDependencies()
    {
        await using var provider = BuildProvider();

        provider.GetService<IAttentionNotifier>().Should().NotBeNull();
        provider.GetService<IPresenceDetector>().Should().NotBeNull();
        provider.GetService<IToastNotifier>().Should().NotBeNull();
        provider.GetService<IWebhookNotifier>().Should().NotBeNull();
        provider.GetService<INotificationSettingsStore>().Should().NotBeNull();
    }

    [Fact]
    public async Task Container_ResolvesTheCockpitViewModel_WithNotificationDependencies()
    {
        // Async disposal: a resolved SessionViewModel is IAsyncDisposable-only, so the provider
        // must be torn down with DisposeAsync.
        await using var provider = BuildProvider();

        provider.GetService<CockpitViewModel>().Should().NotBeNull();
    }
}
