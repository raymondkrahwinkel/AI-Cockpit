using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Services;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.ViewTests;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Backup;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Backup;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Configuration;
using Cockpit.TestSupport;
using CockpitApp = Cockpit.App.App;

namespace Cockpit.Journeys;

// J9, the very first start: a machine that has never run the wizard opens it before the cockpit, asks how to begin,
// and "Start fresh" walks the other steps into the cockpit. The wizard is shown before the plugins' phase, like
// App's first-run route.
[Collection(JourneyCollection.Alone)]
public sealed class FirstStartJourney
{
    [Fact]
    public async Task AFreshMachine_AsksHowToBegin_AndStartingFresh_WalksTheWizardIntoTheCockpit()
    {
        await using var cockpit = JourneyHost.Desktop();
        var services = cockpit.Services;
        var state = services.GetRequiredService<IFirstRunWizardStateStore>();
        Assert.Null(await state.GetCompletedVersionAsync());

        var opened = new TaskCompletionSource<FirstRunWizardWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = Window.WindowOpenedEvent.AddClassHandler<FirstRunWizardWindow>((wizard, _) => opened.TrySetResult(wizard));
        Task shown = Task.CompletedTask;
        await HeadlessAvalonia.RunAsync(() =>
        {
            shown = services.GetRequiredService<IFirstRunWizard>().ShowAsync();
            return Task.CompletedTask;
        });
        var window = await opened.Task.WaitAsync(Until.Ceiling);
        var wizard = HeadlessAvalonia.Run(() => Assert.IsType<FirstRunWizardViewModel>(window.DataContext));

        // The choice step is the second page, with the host's own two ways and no plugin's yet.
        var choice = services.GetServices<IFirstRunWizardStep>().OfType<StartChoiceStep>().Single();
        var (steps, titles, label) = HeadlessAvalonia.Run(() =>
        {
            wizard.NextCommand.Execute(null);
            return (wizard.StepBar.Select(item => item.Title).ToList(), choice.ViewModel.Options.Select(option => option.Title).ToList(), wizard.StepProgressLabel);
        });
        Assert.Equal(["What this is", "Getting started", "What you have", "Your assistant", "What you work on"], steps);
        Assert.Equal(["Start fresh", "Restore a backup file"], titles);
        Assert.Equal("Step 2 of 5", label);

        // Start fresh is the pick nobody has to make: Next carries on through every step, and the last Next ends it.
        await HeadlessAvalonia.RunAsync(() =>
        {
            while (!wizard.IsLastStep)
            {
                wizard.NextCommand.Execute(null);
            }

            wizard.NextCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            return Task.CompletedTask;
        });
        await shown.WaitAsync(Until.Ceiling);
        Assert.Equal(FirstRunWizardVersion.Current, await state.GetCompletedVersionAsync());

        await cockpit.StartDesktopAsync();
        Assert.True(HeadlessAvalonia.Run(() => cockpit.Window.IsVisible));
    }

    // AC-1509, route C1: on the very first start the bundled Depot offers its route before any plugin initialized. Signing
    // in to a fake Depot holding one backup and restoring it restarts into the cockpit it was made of, asking for the sign-in
    // the backup left out (AC-1503).
    [Fact]
    public async Task AFreshMachine_SignsInToItsDepot_AndRestoresItsLatestBackup_IntoTheCockpitItWasMadeOf()
    {
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"journey-first-start-{Guid.NewGuid():N}")).FullName;
        try
        {
            var archive = Path.Combine(work, "made-elsewhere.zip");
            await using (var elsewhere = JourneyHost.Desktop(root => File.WriteAllText(CockpitConfigPath.For(root), MadeElsewhere), Directory.CreateDirectory(Path.Combine(work, "elsewhere")).FullName))
            {
                await elsewhere.Services.GetRequiredService<IPluginRegistrationStore>().SaveDataAsync("depot", new Dictionary<string, string> { ["connections"] = """{"token":"journey-secret"}""" });
                await elsewhere.Services.GetRequiredService<IBackupService>().WriteAsync(archive, new BackupOptions(IncludeCredentials: false));
            }

            var space = new _AccountSpace(await File.ReadAllBytesAsync(archive));
            await using var depot = await LoopbackHttpServer.StartAsync(space.HandleAsync);
            var restarts = new _Restarts();
            var fresh = Directory.CreateDirectory(Path.Combine(work, "fresh")).FullName;
            await using (var cockpit = JourneyHost.Desktop(stateRoot: fresh, configure: services =>
            {
                services.AddSingleton<IMcpOAuthCoordinator>(provider => new _SignedInOnClick(provider.GetRequiredService<IMcpOAuthTokenStore>(), depot.BaseUrl));
                services.AddSingleton<IAppRestartService>(restarts);
            }))
            {
                var services = cockpit.Services;
                HeadlessAvalonia.Run(CockpitApp.InitializeFirstStart);

                var opened = new TaskCompletionSource<FirstRunWizardWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var watch = Window.WindowOpenedEvent.AddClassHandler<FirstRunWizardWindow>((wizard, _) => opened.TrySetResult(wizard));
                await HeadlessAvalonia.RunAsync(() =>
                {
                    _ = services.GetRequiredService<IFirstRunWizard>().ShowAsync();
                    return Task.CompletedTask;
                });
                var window = await opened.Task.WaitAsync(Until.Ceiling);
                var choice = services.GetServices<IFirstRunWizardStep>().OfType<StartChoiceStep>().Single();
                var view = HeadlessAvalonia.Run(() =>
                {
                    ((FirstRunWizardViewModel)(window.DataContext ?? throw new InvalidOperationException("The wizard has no view model."))).NextCommand.Execute(null);
                    Assert.Equal(["Start fresh", "Restore a backup file", "Sign in to a Depot"], choice.ViewModel.Options.Select(option => option.Title));
                    choice.ViewModel.ChooseCommand.Execute(choice.ViewModel.Options[2]);
                    return choice.ViewModel.SelectedContent ?? throw new InvalidOperationException("The Depot route shows nothing.");
                });

                await HeadlessAvalonia.RunAsync(async () =>
                {
                    _Named<TextBox>(view, "DepotFirstStartUrl").Text = depot.BaseUrl;
                    var status = _Named<TextBlock>(view, "DepotFirstStartStatus");
                    _Named<Button>(view, "DepotFirstStartSignIn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until.Holds(status, () => status.Text?.StartsWith("Signed in to", StringComparison.Ordinal) == true);

                    // One backup there and no /cockpit/manifest: C1 is offered, C2 is not.
                    var restore = _Named<Button>(view, "DepotFirstStartRestore");
                    Assert.True(restore.IsEnabled);
                    Assert.False(_Named<Button>(view, "DepotFirstStartSetUp").IsVisible);
                    restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var outcome = _Named<TextBlock>(view, "DepotFirstStartOutcome");
                    await Until.Holds(outcome, () => outcome.Text?.StartsWith("Restored.", StringComparison.Ordinal) == true);
                });

                Assert.True(restarts.Asked);
                Assert.Equal(FirstRunWizardVersion.Current, await services.GetRequiredService<IFirstRunWizardStateStore>().GetCompletedVersionAsync());
            }

            await using var restarted = JourneyHost.Desktop(stateRoot: fresh);
            await restarted.StartDesktopAsync();
            Assert.Equal(333, JsonNode.Parse(await File.ReadAllTextAsync(CockpitConfigPath.For(fresh)))?["Layout"]?["SidebarWidth"]?.GetValue<double>());
            Assert.Contains(HeadlessAvalonia.Run(() => restarted.Cockpit.Toasts.Select(toast => toast.Message).ToList()),
                message => message.Contains("new sign-in", StringComparison.Ordinal) && message.Contains("depot", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            JourneyHost.RemoveStateRoot(work);
        }
    }

    // A cockpit with a sidebar width of its own. Its Depot sign-in is seeded after the build, so the bundled plugin installs.
    private const string MadeElsewhere = """{"Layout":{"SidebarWidth":333}}""";

    private static T _Named<T>(Control root, string name)
        where T : Control =>
        root.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"The Depot route has no {name}.");

    // The browser's half of the sign-in, which a journey cannot run: the click signs in, and the bearer is the fake's. It
    // stores the sign-in as a real one does, so the host narrows its depot.host token from it at the fake's token endpoint.
    private sealed class _SignedInOnClick(IMcpOAuthTokenStore tokens, string url) : IMcpOAuthCoordinator
    {
        public event Action<McpServerConfig>? SignInNeeded
        {
            add { }
            remove { }
        }

        public async Task<McpOAuthAccess> AcquireAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken = default)
        {
            await tokens.SaveAsync(server.IdentityKey, server.Name, DepotHostScope.SignedIn(_AccountSpace.Bearer, url.TrimEnd('/')), cancellationToken);
            return McpOAuthAccess.Authorized(_AccountSpace.Bearer);
        }

        public Task<McpOAuthAccess> AcquireForSessionAsync(McpServerConfig server, CancellationToken cancellationToken = default) =>
            Task.FromResult(McpOAuthAccess.Authorized(_AccountSpace.Bearer));

        public Task<McpOAuthAccess> RenewRejectedAsync(McpServerConfig server, string rejectedAccessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(McpOAuthAccess.AuthorizationRequired);

        public Task<McpAuthState> GetStateAsync(McpServerConfig server, CancellationToken cancellationToken = default) =>
            Task.FromResult(McpAuthState.Authorized);

        public Task SignOutAsync(McpServerConfig server, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class _Restarts : IAppRestartService
    {
        public bool Asked { get; private set; }

        public void Restart() => Asked = true;
    }

    // Depot's /cockpit/backups (DEP-168) holding one backup; /cockpit/manifest is not there, as on a Depot without DEP-266.
    private sealed class _AccountSpace(byte[] backup)
    {
        public const string Bearer = "journey-first-start-bearer";

        private static readonly Guid Id = Guid.NewGuid();

        public async Task HandleAsync(HttpContext context)
        {
            if (await DepotHostScope.TryAnswerAsync(context, Bearer))
            {
                return;
            }

            var path = context.Request.Path;
            if (context.Request.Headers.Authorization != $"Bearer {Bearer}")
            {
                context.Response.StatusCode = 401;
            }
            else if (path == "/cockpit/backups")
            {
                await context.Response.WriteAsJsonAsync(new[] { new { id = Id, size = backup.LongLength, sha256 = "", createdAt = DateTime.UtcNow } });
            }
            else if (path == $"/cockpit/backups/{Id}")
            {
                await context.Response.Body.WriteAsync(backup);
            }
            else
            {
                context.Response.StatusCode = 404;
            }
        }
    }
}
