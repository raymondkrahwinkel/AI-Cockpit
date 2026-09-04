using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Layout;
using Cockpit.Core.Mcp;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.Secrets;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Sessions;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;
using System.Reflection;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The Options dialog as a transaction (AC-999). Nothing the operator changes reaches a store until Apply, and
/// Cancel leaves the cockpit exactly as the dialog found it.
///
/// The stores are watched *while* the values change, not only afterwards: the failure this replaces was a dialog
/// that wrote as you typed, where a test that only checked the end state would have passed on the way to it.
/// </summary>
public class OptionsStagedChangesTests
{
    [Fact]
    public async Task ChangingSettings_WhileStaged_WritesNothingToAnyStore()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        vm.BeginOptionsEdit();

        vm.LocalNotificationsEnabled = !vm.LocalNotificationsEnabled;
        vm.ShowTimestamps = !vm.ShowTimestamps;
        vm.AutoCloseOnExit = !vm.AutoCloseOnExit;
        vm.TerminalFontSize = 20;
        vm.VoiceEnabled = !vm.VoiceEnabled;
        vm.MinimizeToTrayOnClose = !vm.MinimizeToTrayOnClose;

        stores.AssertNothingWasSaved();
    }

    [Fact]
    public async Task Apply_WritesEverySection()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        vm.BeginOptionsEdit();
        vm.LocalNotificationsEnabled = true;
        vm.TerminalFontSize = 20;

        await vm.ApplyOptionsCommand.ExecuteAsync(null);

        await stores.Notifications.Received().SaveAsync(Arg.Any<NotificationSettings>());
        await stores.TranscriptDisplay.Received().SaveAsync(Arg.Any<TranscriptDisplaySettings>());
        await stores.SessionBehavior.Received().SaveAsync(Arg.Any<SessionBehaviorSettings>());
        await stores.Layout.Received().SaveAsync(Arg.Any<LayoutSettings>());
        await stores.Voice.Received().SaveAsync(Arg.Any<VoiceSettings>());
        await stores.Terminal.Received().SaveAsync(Arg.Is<TerminalSettings>(settings => settings.FontSize == 20));
    }

    [Fact]
    public async Task Cancel_PutsEverySettingBackToWhatWasOnDisk()
    {
        var stores = new Stores();
        stores.Notifications.LoadAsync().Returns(new NotificationSettings { LocalEnabled = true, WebhookUrl = "https://hooks.example/x" });
        stores.Terminal.LoadAsync().Returns(new TerminalSettings { FontSize = 15, FontFamily = "Cascadia Mono", Shell = "" });
        // AC-1086: the shared memory budget decides when the cockpit speaks up, so a Cancel that leaves a changed
        // one standing is a warning threshold the operator did not agree to.
        stores.SessionBehavior.LoadAsync().Returns(new SessionBehaviorSettings { MemoryBudgetPercent = 60 });
        var vm = await stores.NewViewModelAsync();
        vm.BeginOptionsEdit();

        vm.LocalNotificationsEnabled = false;
        vm.WebhookUrl = "https://hooks.example/somewhere-else";
        vm.TerminalFontSize = 20;
        vm.MemoryBudgetPercent = 95;

        await vm.CancelOptionsCommand.ExecuteAsync(null);

        Assert.True(vm.LocalNotificationsEnabled);
        Assert.Equal("https://hooks.example/x", vm.WebhookUrl);
        Assert.Equal(15, vm.TerminalFontSize);
        Assert.Equal(60, vm.MemoryBudgetPercent);
        stores.AssertNothingWasSaved();
    }

    [Fact]
    public async Task Cancel_UndoesRestoreDefaults()
    {
        var stores = new Stores();
        stores.Notifications.LoadAsync().Returns(new NotificationSettings { LocalEnabled = true, WebhookUrl = "https://hooks.example/x" });
        var vm = await stores.NewViewModelAsync();
        vm.BeginOptionsEdit();

        vm.RestoreOptionDefaultsCommand.Execute(null);
        Assert.Equal(string.Empty, vm.WebhookUrl);

        await vm.CancelOptionsCommand.ExecuteAsync(null);

        Assert.Equal("https://hooks.example/x", vm.WebhookUrl);
        stores.AssertNothingWasSaved();
    }

    [Fact]
    public async Task PendingChanges_AppearOnAChange_AndGoAwayWhenTheValueIsPutBack()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        vm.BeginOptionsEdit();
        Assert.False(vm.HasPendingOptionChanges);

        var original = vm.MinimizeToTrayOnClose;
        vm.MinimizeToTrayOnClose = !original;
        Assert.True(vm.HasPendingOptionChanges);

        vm.MinimizeToTrayOnClose = original;
        Assert.False(vm.HasPendingOptionChanges);
    }

    [Fact]
    public async Task PendingChanges_AreClearedByApplyAndByCancel()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();

        vm.BeginOptionsEdit();
        vm.MinimizeToTrayOnClose = !vm.MinimizeToTrayOnClose;
        await vm.ApplyOptionsCommand.ExecuteAsync(null);
        Assert.False(vm.HasPendingOptionChanges);

        vm.BeginOptionsEdit();
        vm.MinimizeToTrayOnClose = !vm.MinimizeToTrayOnClose;
        await vm.CancelOptionsCommand.ExecuteAsync(null);
        Assert.False(vm.HasPendingOptionChanges);
    }

    [Fact]
    public async Task DiscardConfirmation_IsRequiredForDisplayOnlyChanges()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        vm.BeginOptionsEdit();

        vm.ShowTimestamps = !vm.ShowTimestamps;

        Assert.True(vm.ShouldConfirmOptionDiscard());
    }

    [Fact]
    public async Task DiscardConfirmation_IsKeptForIntegrationChanges()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        vm.BeginOptionsEdit();

        vm.WebhookUrl = "https://hooks.example/changed";

        Assert.True(vm.ShouldConfirmOptionDiscard());
    }

    [Fact]
    public async Task UsageThresholds_AreWrittenByApply_RatherThanAfterTheDialogHasClosed()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        var thresholdStore = Substitute.For<IUsageThresholdStore>();
        thresholdStore.LoadAsync().Returns(new UsageThresholdSettings());
        var thresholds = new UsageThresholdsViewModel(thresholdStore);
        await thresholds.LoadAsync([("anthropic", "Anthropic", [new PluginUsageSignal("weekly", "Weekly", PluginUsageSignalKind.Allowance, 80)])]);
        vm.UsageThresholdSettings = thresholds;

        vm.BeginOptionsEdit();
        thresholds.Providers[0].Signals[0].Threshold = 55;
        await thresholdStore.DidNotReceive().SaveAsync(Arg.Any<UsageThresholdSettings>());

        await vm.ApplyOptionsCommand.ExecuteAsync(null);

        await thresholdStore.Received().SaveAsync(Arg.Any<UsageThresholdSettings>());
    }

    [Fact]
    public async Task PendingChanges_NoticeAThresholdRow_WhichRaisesNothingThisViewModelHears()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        var thresholdStore = Substitute.For<IUsageThresholdStore>();
        thresholdStore.LoadAsync().Returns(new UsageThresholdSettings());
        var thresholds = new UsageThresholdsViewModel(thresholdStore);
        await thresholds.LoadAsync([("anthropic", "Anthropic", [new PluginUsageSignal("weekly", "Weekly", PluginUsageSignalKind.Allowance, 80)])]);
        vm.UsageThresholdSettings = thresholds;
        vm.BeginOptionsEdit();

        thresholds.Providers[0].Signals[0].Threshold = 55;

        Assert.True(vm.RefreshPendingOptionChanges(), "closing must not discard a threshold edit without asking");
    }

    [Fact]
    public async Task PendingChanges_NoticeAShortcutRow()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        vm.BeginOptionsEdit();

        vm.ShortcutRows[0].Gesture = "Ctrl+Alt+Shift+F12";

        Assert.True(vm.RefreshPendingOptionChanges(), "closing must not discard a shortcut edit without asking");
    }

    [Fact]
    public async Task UsageThresholds_AreRolledBackByCancel()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        var thresholdStore = Substitute.For<IUsageThresholdStore>();
        thresholdStore.LoadAsync().Returns(new UsageThresholdSettings());
        var thresholds = new UsageThresholdsViewModel(thresholdStore);
        await thresholds.LoadAsync([("anthropic", "Anthropic", [new PluginUsageSignal("weekly", "Weekly", PluginUsageSignalKind.Allowance, 80)])]);
        vm.UsageThresholdSettings = thresholds;

        vm.BeginOptionsEdit();
        thresholds.Providers[0].Signals[0].Threshold = 55;

        await vm.CancelOptionsCommand.ExecuteAsync(null);

        Assert.Null(thresholds.Providers[0].Signals[0].Threshold);
        await thresholdStore.DidNotReceive().SaveAsync(Arg.Any<UsageThresholdSettings>());
    }

    [Fact]
    public async Task SecurityToggles_AreHeldBackWhileSuspended_AndWrittenOnApply()
    {
        var screenLock = Substitute.For<IScreenLockSettingsStore>();
        screenLock.LoadAsync().Returns(new ScreenLockSettings());
        var terminalAccess = Substitute.For<ITerminalAccessSettingsStore>();
        terminalAccess.LoadAsync().Returns(new TerminalAccessSettings());
        var security = new SecurityOptionsViewModel(
            Substitute.For<ISecretProtectionService>(),
            screenLock,
            terminalAccessSettings: terminalAccess)
        {
            SuspendPersistence = true,
        };

        security.LockWithOperatingSystem = false;
        security.TerminalAccessEnabled = true;

        await screenLock.DidNotReceive().SaveAsync(Arg.Any<ScreenLockSettings>());
        await terminalAccess.DidNotReceive().SaveAsync(Arg.Any<TerminalAccessSettings>());

        security.SuspendPersistence = false;
        await security.SaveStagedAsync();

        await screenLock.Received().SaveAsync(Arg.Is<ScreenLockSettings>(s => !s.LockWhenOperatingSystemLocks));
        await terminalAccess.Received().SaveAsync(Arg.Is<TerminalAccessSettings>(s => s.Enabled));
    }

    [Fact]
    public async Task SecurityToggles_ComeBackFromDiskOnCancel()
    {
        var screenLock = Substitute.For<IScreenLockSettingsStore>();
        screenLock.LoadAsync().Returns(new ScreenLockSettings { LockWhenOperatingSystemLocks = true });
        var security = new SecurityOptionsViewModel(Substitute.For<ISecretProtectionService>(), screenLock)
        {
            SuspendPersistence = true,
        };

        security.LockWithOperatingSystem = false;
        await security.RefreshAsync();

        Assert.True(security.LockWithOperatingSystem);
        await screenLock.DidNotReceive().SaveAsync(Arg.Any<ScreenLockSettings>());
    }

    [Fact]
    public async Task AssistantSettings_AreHeldBackWhileSuspended_AndWrittenOnApply()
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync().Returns(new AssistantSettings());
        var assistant = new AssistantOptionsViewModel(store) { SuspendPersistence = true };

        assistant.IsEnabled = true;
        assistant.SpeakReplies = false;

        await store.DidNotReceive().SaveAsync(Arg.Any<AssistantSettings>());

        assistant.SuspendPersistence = false;
        await assistant.SaveStagedAsync();

        await store.Received().SaveAsync(Arg.Is<AssistantSettings>(settings => settings.IsEnabled && !settings.SpeakReplies));
    }

    [Fact]
    public async Task AssistantSettings_ComeBackFromDiskOnCancel()
    {
        var store = Substitute.For<IAssistantSettingsStore>();
        store.LoadAsync().Returns(new AssistantSettings { IsEnabled = true, SpeakReplies = true });
        var assistant = new AssistantOptionsViewModel(store) { SuspendPersistence = true };

        assistant.IsEnabled = false;
        assistant.SpeakReplies = false;
        await assistant.RefreshAsync();

        Assert.True(assistant.IsEnabled);
        Assert.True(assistant.SpeakReplies);
        await store.DidNotReceive().SaveAsync(Arg.Any<AssistantSettings>());
    }

    // Profiles (AC-1001): the category is a `ManageProfilesDialogViewModel` handed to the cockpit rather than a
    // scalar property, so it is exercised separately from the fingerprint-driven scalars above — same contract
    // (nothing written until Apply, Cancel puts it back exactly), proven at the level the dialog actually drives.
    [Fact]
    public async Task Profiles_AreWrittenByApply_RatherThanImmediately()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([new SessionProfile("work", new OllamaConfig("http://localhost:11434", "llama3.1"))]);
        var profiles = new ManageProfilesDialogViewModel(profileStore, Substitute.For<IProfileLoginChecker>());
        await profiles.LoadAsync();
        vm.Profiles = profiles;

        vm.BeginOptionsEdit();
        profiles.SelectedProfile!.Purpose = "renamed purpose";
        await profileStore.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<SessionProfile>>(), Arg.Any<CancellationToken>());

        await vm.ApplyOptionsCommand.ExecuteAsync(null);

        await profileStore.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<SessionProfile>>(list => list.Count == 1 && list[0].Purpose == "renamed purpose"),
            Arg.Any<CancellationToken>());
    }

    // Criterion 6: a removal is staged like any other edit — Cancel puts the row back, exactly as it puts a typo
    // back, rather than the standalone dialog's old immediate-and-persisted Remove.
    [Fact]
    public async Task Profiles_ARemovedRowComesBackOnCancel_AndNothingIsWritten()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        var profileStore = Substitute.For<ISessionProfileStore>();
        profileStore.LoadAsync(Arg.Any<CancellationToken>()).Returns([new SessionProfile("work", new OllamaConfig("http://localhost:11434", "llama3.1"))]);
        var profiles = new ManageProfilesDialogViewModel(profileStore, Substitute.For<IProfileLoginChecker>());
        await profiles.LoadAsync();
        vm.Profiles = profiles;

        vm.BeginOptionsEdit();
        profiles.SelectedProfile = profiles.Profiles.Single();
        profiles.RemoveProfileCommand.Execute(null);
        profiles.ConfirmRemoveCommand.Execute(null);
        Assert.Empty(profiles.Profiles);

        await vm.CancelOptionsCommand.ExecuteAsync(null);

        Assert.Equal("work", Assert.Single(profiles.Profiles).Label);
        await profileStore.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<SessionProfile>>(), Arg.Any<CancellationToken>());
    }

    // AC-1082: a rejecting profile used to block the whole Apply, silently — PluginSettingsError stayed empty.
    // Now only Profiles is held back, everything else the operator changed still saves on the same click, and
    // the refusal is no longer silent.
    [Fact]
    public async Task Profiles_AnInvalidProfile_BlocksOnlyProfiles_AndStillSavesTheRest()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        var profileStore = Substitute.For<ISessionProfileStore>();
        var profiles = new ManageProfilesDialogViewModel(profileStore, Substitute.For<IProfileLoginChecker>());
        vm.Profiles = profiles;

        vm.BeginOptionsEdit();
        profiles.AddProfileCommand.Execute(null); // an empty new profile: invalid until configured
        vm.LocalNotificationsEnabled = true; // an unrelated, otherwise-valid change

        await vm.ApplyOptionsCommand.ExecuteAsync(null);

        Assert.True(vm.OptionsApplyBlocked);
        await profileStore.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<SessionProfile>>(), Arg.Any<CancellationToken>());
        await stores.Notifications.Received(1).SaveAsync(Arg.Any<NotificationSettings>());
        Assert.StartsWith("Profiles: ", vm.PluginSettingsError);
        Assert.Equal("profiles", vm.OptionsApplyBlockedCategoryTag);
    }

    // AC-1082: MCP Servers refusing used to block the perfectly valid edited profile too. Now each section
    // stands on its own: the profile commits, only MCP Servers is held back. Price named in the grooming: a
    // section that already committed can no longer be undone by a Cancel on the same blocked Apply.
    [Fact]
    public async Task McpRejection_StillWritesTheValidProfile_AndOnlyMcpServersIsHeldBack()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        var directory = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var profileFile = Path.Combine(directory, "cockpit.json");

        try
        {
            var profileStore = new SessionProfileStore(profileFile);
            await profileStore.SaveAsync([new SessionProfile("work", new OllamaConfig("http://localhost:11434", "llama3.1"), Purpose: "original")]);
            var profiles = new ManageProfilesDialogViewModel(profileStore, Substitute.For<IProfileLoginChecker>());
            await profiles.LoadAsync();
            vm.Profiles = profiles;

            var mcpStore = Substitute.For<IMcpServerStore>();
            var mcpServers = new McpServersViewModel(mcpStore, []);
            mcpServers.AddServerCommand.Execute(null);
            mcpServers.SelectedServer!.Name = "incomplete";
            mcpServers.SelectedServer.Command = string.Empty;
            mcpServers.SelectedServer.Url = string.Empty;
            vm.McpServers = mcpServers;

            vm.BeginOptionsEdit();
            profiles.SelectedProfile!.Purpose = "edited";

            await vm.ApplyOptionsCommand.ExecuteAsync(null);

            Assert.True(vm.OptionsApplyBlocked);
            Assert.StartsWith("MCP Servers: ", vm.PluginSettingsError);
            Assert.Equal("mcp-servers", vm.OptionsApplyBlockedCategoryTag);
            await mcpStore.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<McpServerConfig>>(), Arg.Any<CancellationToken>());

            var reloaded = new SessionProfileStore(profileFile);
            Assert.Equal("edited", (await reloaded.LoadAsync()).Single().Purpose);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task McpConfirmationReadFailure_DoesNotBlockAnApplyThatAlreadySaved()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        var mcpStore = Substitute.For<IMcpServerStore>();
        mcpStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromException<IReadOnlyList<McpServerConfig>>(new IOException()));
        var mcpServers = new McpServersViewModel(mcpStore, []);
        mcpServers.AddServerCommand.Execute(null);
        mcpServers.SelectedServer!.Name = "filesystem";
        mcpServers.SelectedServer.Command = "npx";
        vm.McpServers = mcpServers;

        vm.BeginOptionsEdit();
        await vm.ApplyOptionsCommand.ExecuteAsync(null);

        Assert.False(vm.OptionsApplyBlocked);
        await mcpStore.Received(1).SaveAsync(Arg.Any<IReadOnlyList<McpServerConfig>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReopeningOptions_PreservesPendingChanges()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        vm.BeginOptionsEdit();
        vm.MinimizeToTrayOnClose = !vm.MinimizeToTrayOnClose;

        vm.BeginOptionsEdit();

        Assert.True(vm.HasPendingOptionChanges);
    }

    [Fact]
    public async Task ClosingOptionsAfterTwoOpens_RemovesTheStagedSubscription()
    {
        var stores = new Stores();
        var vm = await stores.NewViewModelAsync();
        vm.BeginOptionsEdit();
        Assert.Contains(_PropertyChangedHandlers(vm), handler => handler.Method.Name == "_OnStagedPropertyChanged");

        vm.BeginOptionsEdit();

        await vm.CancelOptionsCommand.ExecuteAsync(null);

        Assert.DoesNotContain(_PropertyChangedHandlers(vm), handler => handler.Method.Name == "_OnStagedPropertyChanged");
    }

    private static IEnumerable<Delegate> _PropertyChangedHandlers(CockpitViewModel viewModel)
    {
        for (Type? type = viewModel.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(viewModel) is MulticastDelegate handlers)
            {
                return handlers.GetInvocationList();
            }
        }

        return [];
    }

    // Every store the dialog can write to, stubbed to return defaults and watched for writes.
    private sealed class Stores
    {
        public INotificationSettingsStore Notifications { get; } = Substitute.For<INotificationSettingsStore>();

        public ITranscriptDisplaySettingsStore TranscriptDisplay { get; } = Substitute.For<ITranscriptDisplaySettingsStore>();

        public ISessionBehaviorSettingsStore SessionBehavior { get; } = Substitute.For<ISessionBehaviorSettingsStore>();

        public ILayoutSettingsStore Layout { get; } = Substitute.For<ILayoutSettingsStore>();

        public IVoiceSettingsStore Voice { get; } = Substitute.For<IVoiceSettingsStore>();

        public ITerminalSettingsStore Terminal { get; } = Substitute.For<ITerminalSettingsStore>();

        public Stores()
        {
            Notifications.LoadAsync().Returns(new NotificationSettings());
            TranscriptDisplay.LoadAsync().Returns(new TranscriptDisplaySettings());
            SessionBehavior.LoadAsync().Returns(new SessionBehaviorSettings());
            Layout.LoadAsync().Returns(new LayoutSettings());
            Voice.LoadAsync().Returns(new VoiceSettings());
            Terminal.LoadAsync().Returns(new TerminalSettings());
        }

        public async Task<CockpitViewModel> NewViewModelAsync()
        {
            // The constructor seeds itself from these stores in the background. Reverting once is the public way to
            // wait for that to have finished, so a test is not racing a load still on its way in.
            var viewModel = new CockpitViewModel(
                () => new SessionViewModel(),
                () => new TtyViewModel(),
                Substitute.For<ISessionDialogService>(),
                Substitute.For<IAudioCaptureService>(),
                Substitute.For<IAudioPlaybackService>(),
                Substitute.For<IAttentionNotifier>(),
                Notifications,
                TranscriptDisplay,
                SessionBehavior,
                Layout,
                Voice,
                Terminal);

            viewModel.BeginOptionsEdit();
            await viewModel.CancelOptionsCommand.ExecuteAsync(null);
            return viewModel;
        }

        public void AssertNothingWasSaved()
        {
            Notifications.DidNotReceive().SaveAsync(Arg.Any<NotificationSettings>());
            TranscriptDisplay.DidNotReceive().SaveAsync(Arg.Any<TranscriptDisplaySettings>());
            SessionBehavior.DidNotReceive().SaveAsync(Arg.Any<SessionBehaviorSettings>());
            Layout.DidNotReceive().SaveAsync(Arg.Any<LayoutSettings>());
            Voice.DidNotReceive().SaveAsync(Arg.Any<VoiceSettings>());
            Terminal.DidNotReceive().SaveAsync(Arg.Any<TerminalSettings>());
        }
    }
}
