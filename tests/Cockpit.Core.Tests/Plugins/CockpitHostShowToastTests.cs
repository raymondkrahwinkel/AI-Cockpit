using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

// CockpitHost.ShowToast (AC-1074): an error toast also lands in the log. A toast is gone in seconds, and a channel
// plugin reporting "nothing will ever come in" is exactly what someone reads back an hour later.
public class CockpitHostShowToastTests
{
    [Fact]
    public void AnErrorToast_IsAlsoLogged_TaggedWithThePluginId()
    {
        var logger = new _RecordingLogger();
        ICockpitHost host = _BuildHost(logger);

        host.ShowToast("no message will reach the assistant", PluginToastSeverity.Error);

        var (level, message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, level);
        Assert.Contains("slack", message, StringComparison.Ordinal);
        Assert.Contains("no message will reach the assistant", message, StringComparison.Ordinal);
    }

    // Only errors: an information toast is chatter, and logging every one of them would bury the errors that matter.
    [Theory]
    [InlineData(PluginToastSeverity.Information)]
    [InlineData(PluginToastSeverity.Success)]
    [InlineData(PluginToastSeverity.Warning)]
    public void AToastBelowError_IsNotLogged(PluginToastSeverity severity)
    {
        var logger = new _RecordingLogger();
        ICockpitHost host = _BuildHost(logger);

        host.ShowToast("saved", severity);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void AToast_StillReachesTheToastService()
    {
        var toasts = Substitute.For<IToastService>();
        ICockpitHost host = _BuildHost(new _RecordingLogger(), toasts);

        host.ShowToast("no message will reach the assistant", PluginToastSeverity.Error);

        toasts.Received(1).Show("no message will reach the assistant", Arg.Any<ToastSeverity>(), null, null);
    }

    private static CockpitHost _BuildHost(ILogger<CockpitHost> logger, IToastService? toasts = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(toasts ?? Substitute.For<IToastService>());
        services.AddSingleton(logger);

        return new CockpitHost(
            "slack",
            "Slack",
            services.BuildServiceProvider(),
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics());
    }

    private sealed class _RecordingLogger : ILogger<CockpitHost>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
