using Cockpit.App.ViewModels.Onboarding;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using NSubstitute;

namespace Cockpit.App.ViewTests.Onboarding;

// AC-511: the catalogue goes through the real PluginStoreIndex.TryParse; a hand-built entry proves a shape no disk file has.
[Collection("avalonia")]
public class WorkKindStepViewModelTests
{
    private const string IndexWithAudience = """
    {
      "name": "Example Store",
      "plugins": [
        { "id": "github-issues", "name": "GitHub Issues", "author": "Cockpit", "latestVersion": "1.1.0", "audience": ["developer"],
          "versions": [ { "version": "1.1.0", "path": "github-issues/gh-1.1.0.zip", "abstractionsVersion": 1, "sha256": "aaa111" } ] },
        { "id": "pull-requests", "name": "GitHub Pull Requests", "author": "Cockpit", "latestVersion": "2.0.0", "audience": ["developer"],
          "versions": [ { "version": "2.0.0", "path": "pull-requests/pr-2.0.0.zip", "abstractionsVersion": 1, "sha256": "bbb222" } ] },
        { "id": "clock", "name": "Clock", "author": "Cockpit", "latestVersion": "0.9.0",
          "versions": [ { "version": "0.9.0", "path": "clock/clock-0.9.0.zip", "abstractionsVersion": 1 } ] },
        { "id": "example-companion-tool", "name": "Example Companion Tool", "author": "Cockpit", "latestVersion": "0.1.0",
          "versions": [ { "version": "0.1.0", "path": "example-companion-tool/example-0.1.0.zip", "abstractionsVersion": 1 } ] },
        { "id": "example-workspace", "name": "Example Workspace", "author": "Cockpit", "latestVersion": "0.1.0",
          "versions": [ { "version": "0.1.0", "path": "example-workspace/example-0.1.0.zip", "abstractionsVersion": 1 } ] },
        { "id": "claude-provider", "name": "Claude", "author": "Cockpit", "latestVersion": "1.0.0", "category": "AI providers",
          "versions": [ { "version": "1.0.0", "path": "claude-provider/claude-1.0.0.zip", "abstractionsVersion": 1 } ] }
      ]
    }
    """;

    // An index as published before AC-511 added the field — no audience anywhere (criterion 5).
    private const string IndexBeforeAudienceExisted = """
    {
      "name": "Example Store",
      "plugins": [
        { "id": "github-issues", "name": "GitHub Issues", "author": "Cockpit", "latestVersion": "1.1.0",
          "versions": [ { "version": "1.1.0", "path": "github-issues/gh-1.1.0.zip", "abstractionsVersion": 1, "sha256": "aaa111" } ] }
      ]
    }
    """;

    private static readonly PluginStoreConfig Store = PluginStoreConfig.Remote("https://plugins.example.org/index.json");

    private readonly IPluginProvisioningService _provisioning = Substitute.For<IPluginProvisioningService>();
    private readonly IPluginRegistrationStore _registrations = Substitute.For<IPluginRegistrationStore>();
    private readonly List<PluginProvisionRequest> _sentToInstall = [];

    private PluginProvisionBatchResult _batchResult = new([]);

    public WorkKindStepViewModelTests()
    {
        _provisioning
            .InstallManyAsync(
                Arg.Do<IReadOnlyList<PluginProvisionRequest>>(_sentToInstall.AddRange),
                Arg.Any<int>(), Arg.Any<Version?>(), Arg.Any<CancellationToken>())
            .Returns(_ => _batchResult);
    }

    [Fact]
    public async Task ATick_StaysTheOperatorsToChange_AfterAWorkKindHasSetIt()
    {
        var viewModel = _ViewModel();
        await viewModel.LoadAsync();
        viewModel.SelectedWorkKind = _Kind(PluginWorkKinds.Developer);

        viewModel.Plugins.Single(row => row.Name == "GitHub Pull Requests").IsSelected = false;
        viewModel.Plugins.Single(row => row.Name == "Clock").IsSelected = false;
        await viewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "github-issues" }, _sentToInstall.Select(request => request.Id));
    }

    // The pre-tick is a suggestion, not a decision: leaving the step by Skip or Next never reaches the confirm command.
    [Fact]
    public async Task LeavingTheStepWithoutConfirming_InstallsNothing()
    {
        var viewModel = _ViewModel();
        await viewModel.LoadAsync();

        viewModel.SelectedWorkKind = _Kind(PluginWorkKinds.Developer);

        await _provisioning.DidNotReceive().InstallManyAsync(
            Arg.Any<IReadOnlyList<PluginProvisionRequest>>(), Arg.Any<int>(), Arg.Any<Version?>(), Arg.Any<CancellationToken>());
        Assert.Empty(_sentToInstall);
    }

    [Fact]
    public async Task Confirm_WhenOnePluginFails_KeepsTheRest_AndSaysWhichDidNotLand()
    {
        _batchResult = new PluginProvisionBatchResult(
        [
            new PluginProvisionResult(PluginProvisionOutcome.Installed, "github-issues", "GitHub Issues", null, null, "github-issues", "aaa111"),
            new PluginProvisionResult(PluginProvisionOutcome.Failed, "pull-requests", "GitHub Pull Requests", "Download failed.", null, null, null),
        ]);
        var viewModel = _ViewModel();
        await viewModel.LoadAsync();
        viewModel.SelectedWorkKind = _Kind(PluginWorkKinds.Developer);
        viewModel.Plugins.Single(row => row.Name == "Clock").IsSelected = false;

        await viewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("1 of 2 installed", viewModel.Summary ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("GitHub Pull Requests (Download failed.)", viewModel.Summary ?? string.Empty, StringComparison.Ordinal);
        await _registrations.Received(1).SaveAsync("github-issues", new PluginRegistration(true, "aaa111"), Arg.Any<CancellationToken>());
        await _registrations.DidNotReceive().SaveAsync("pull-requests", Arg.Any<PluginRegistration>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnIndexWithoutTheAudienceField_LeavesTheStepWorking_WithNothingSuggested()
    {
        var viewModel = _ViewModel(IndexBeforeAudienceExisted);

        await viewModel.LoadAsync();

        Assert.False(viewModel.HasRecommendations);
        Assert.Equal(new[] { "GitHub Issues" }, viewModel.Plugins.Select(row => row.Name));
        Assert.DoesNotContain(viewModel.Plugins, row => row.IsSelected);
    }

    private static PluginWorkKindOption _Kind(string key) =>
        PluginWorkKinds.All.Single(option => option.Key == key);

    private WorkKindStepViewModel _ViewModel(string indexJson = IndexWithAudience) =>
        new(_StoreConfig(), _StoreClient(indexJson), _provisioning, _registrations);

    private static IPluginStoreConfigStore _StoreConfig()
    {
        var store = Substitute.For<IPluginStoreConfigStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<PluginStoreConfig>>([Store]);

        return store;
    }

    private static IPluginStoreClient _StoreClient(string indexJson)
    {
        // Through the real parser on purpose: a substitute returning a hand-built index would prove the step reads
        // a shape no published index has to produce.
        Assert.True(PluginStoreIndex.TryParse(indexJson, out var index, out var error), error);

        var client = Substitute.For<IPluginStoreClient>();
        client.FetchIndexAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<CancellationToken>())
            .Returns(new PluginStoreFetchResult(true, null, index, "https://plugins.example.org/index.json"));

        return client;
    }
}
