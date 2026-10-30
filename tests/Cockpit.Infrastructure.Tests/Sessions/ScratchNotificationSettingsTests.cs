using Cockpit.Core.Notifications;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Tests.Sessions;

public sealed class ScratchNotificationSettingsTests
{
    [Fact]
    public void NotificationSettingsRoundtripPreservesPatternsAndTimeout_DefaultsCoverInstallerFolders()
    {
        var defaults = new NotificationSettingsEntry().ToDomain();
        Assert.Equal(["Config.Msi", "MSI*.tmp", "$Recycle.Bin", "System Volume Information"], defaults.IgnoredHomeFolderPatterns);
        Assert.Equal(5, defaults.ToastTimeoutSeconds);
        var settings = new NotificationSettings { IgnoredHomeFolderPatterns = ["custom-*"], ToastTimeoutSeconds = 17 };
        var restored = NotificationSettingsEntry.FromDomain(settings).ToDomain();
        Assert.Equal(["custom-*"], restored.IgnoredHomeFolderPatterns);
        Assert.Equal(17, restored.ToastTimeoutSeconds);
        Assert.Equal(5, new NotificationSettingsEntry { ToastTimeoutSeconds = 0 }.ToDomain().ToastTimeoutSeconds);
    }
}
