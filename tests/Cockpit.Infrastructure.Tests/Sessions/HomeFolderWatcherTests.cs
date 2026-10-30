using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Core.Notifications;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

public sealed class HomeFolderWatcherTests
{
    [Fact]
    public void NewVisibleDirectoryReportsOnceWithUnknownCreator_IgnoredDirectoryFileAndNoSessionDoNot()
    {
        var defaults = new NotificationSettingsEntry().ToDomain();
        Assert.Equal(["Config.Msi", "MSI*.tmp", "$Recycle.Bin", "System Volume Information"], defaults.IgnoredHomeFolderPatterns);
        Assert.Equal(5, defaults.ToastTimeoutSeconds);
        var settings = new NotificationSettings { IgnoredHomeFolderPatterns = ["custom-*"], ToastTimeoutSeconds = 17 };
        var restored = NotificationSettingsEntry.FromDomain(settings).ToDomain();
        Assert.Equal(["custom-*"], restored.IgnoredHomeFolderPatterns);
        Assert.Equal(17, restored.ToastTimeoutSeconds);
        Assert.Equal(5, new NotificationSettingsEntry { ToastTimeoutSeconds = 0 }.ToDomain().ToastTimeoutSeconds);

        Assert.True(HomeFolderWatcher.ShouldWatchDrive(DriveType.Fixed));
        Assert.False(HomeFolderWatcher.ShouldWatchDrive(DriveType.Network));
        Assert.False(HomeFolderWatcher.ShouldWatchDrive(DriveType.Removable));
        Assert.False(HomeFolderWatcher.ShouldWatchDrive(DriveType.CDRom));

        var root = Path.Combine(Environment.GetEnvironmentVariable("COCKPIT_SCRATCH") ?? Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var visible = Directory.CreateDirectory(Path.Combine(root, "spike-x")).FullName;
        var hidden = Directory.CreateDirectory(Path.Combine(root, ".cache-x")).FullName;
        var file = Path.Combine(root, "file.txt");
        File.WriteAllText(file, "test");
        try
        {
            var registry = Substitute.For<ISessionRegistry>();
            registry.All.Returns(Array.Empty<ISessionHandle>());
            registry.Assistant.Returns((ISessionHandle?)null);
            var toasts = Substitute.For<IToastService>();
            using var watcher = new HomeFolderWatcher(NullLogger<HomeFolderWatcher>.Instance, toasts);
            watcher.Start(registry);
            watcher.ReportCreated(visible);
            Assert.Empty(toasts.ReceivedCalls());

            var session = Substitute.For<ISessionHandle>();
            session.Title.Returns("AC-1820 builder");
            session.PaneId.Returns("builder");
            registry.All.Returns(new[] { session });
            var ignored = Directory.CreateDirectory(Path.Combine(root, "MSI96f57.tmp")).FullName;
            watcher.ReportCreated(ignored);
            watcher.ReportCreated(hidden);
            watcher.ReportCreated(file);
            Assert.Empty(toasts.ReceivedCalls());

            watcher.ReportCreated(visible);
            watcher.ReportCreated(visible);
            var keyPath = Path.GetFullPath(visible);
            toasts.Received(1).Show(
                Arg.Is<string>(message => message.Contains(visible) && message.Contains("Creator unknown.") && !message.Contains("builder")),
                ToastSeverity.Information, Arg.Any<IReadOnlyList<ToastAction>>(), false, "Cockpit",
                Arg.Is<string>(key => string.Equals(key, "home-folder:" + keyPath, StringComparison.OrdinalIgnoreCase)),
                "New folder outside session scratch");
            Assert.Single(toasts.ReceivedCalls());

            var renamed = Path.GetFullPath(Path.Combine(root, "renamed-spike-x"));
            Directory.Move(hidden, renamed);
            watcher.ReportCreated(renamed);
            toasts.Received(1).Show(
                Arg.Is<string>(message => message.Contains(renamed) && message.Contains("Creator unknown.") && !message.Contains("builder")),
                ToastSeverity.Information, Arg.Any<IReadOnlyList<ToastAction>>(), false, "Cockpit",
                Arg.Any<string>(), "New folder outside session scratch");
            Assert.Equal(2, toasts.ReceivedCalls().Count());

            Directory.Move(renamed, hidden);
            watcher.ReportCreated(hidden);
            Assert.Equal(2, toasts.ReceivedCalls().Count());
            Assert.True(Directory.Exists(visible));
            Assert.True(Directory.Exists(hidden));
            Assert.True(File.Exists(file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
