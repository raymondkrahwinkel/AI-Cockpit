using Cockpit.App.ViewModels.Onboarding;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using NSubstitute;

namespace Cockpit.App.ViewTests.Onboarding;

/// <summary>
/// The work-kind step's own behaviour (AC-511): a work kind ticks boxes and nothing else, every tick stays the
/// operator's, only the explicit confirmation installs anything, a half-failed batch reports both halves, and an
/// index published before the work-kind field still drives a working step.
/// </summary>
/// <remarks>
/// The catalogue always arrives through the real <see cref="PluginStoreIndex.TryParse"/>: a substitute handing back
/// a hand-built <c>PluginStoreEntry</c> would prove the view model reads a shape nothing on disk has to produce,
/// which is the one thing these tests are not allowed to assume.
/// </remarks>
[Collection("avalonia")]
public class WorkKindStepViewModelTests : IDisposable
{
    private const string IndexWithWorkKinds = """
    {
      "name": "Example Store",
      "plugins": [
        { "id": "github-issues", "name": "GitHub Issues", "author": "Cockpit", "latestVersion": "1.1.0", "workKind": "developer",
          "versions": [ { "version": "1.1.0", "path": "github-issues/gh-1.1.0.zip", "abstractionsVersion": 1, "sha256": "aaa111" } ] },
        { "id": "pull-requests", "name": "GitHub Pull Requests", "author": "Cockpit", "latestVersion": "2.0.0", "workKind": "developer",
          "versions": [ { "version": "2.0.0", "path": "pull-requests/pr-2.0.0.zip", "abstractionsVersion": 1, "sha256": "bbb222" } ] },
        { "id": "invoices", "name": "Invoices", "author": "Cockpit", "latestVersion": "0.9.0", "workKind": "administration",
          "versions": [ { "version": "0.9.0", "path": "invoices/inv-0.9.0.zip", "abstractionsVersion": 1 } ] }
      ]
    }
    """;

    /// <summary>An index as published before AC-511 added the field — no <c>workKind</c> anywhere (criterion 5).</summary>
    private const string IndexBeforeWorkKindExisted = """
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
    private readonly string _tempDirectory;
    private readonly string _configFilePath;

    private PluginProvisionBatchResult _batchResult = new([]);

    public WorkKindStepViewModelTests()
    {
        _provisioning
            .InstallManyAsync(
                Arg.Do<IReadOnlyList<PluginProvisionRequest>>(_sentToInstall.AddRange),
                Arg.Any<int>(), Arg.Any<Version?>(), Arg.Any<CancellationToken>())
            .Returns(_ => _batchResult);

        _tempDirectory = Path.Combine(Path.GetTempPath(), "cockpit-work-kind-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _configFilePath = Path.Combine(_tempDirectory, "cockpit.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ChoosingAWorkKind_TicksThePluginsItNames_AndNothingElse()
    {
        var viewModel = _ViewModel();
        await viewModel.LoadAsync();

        viewModel.SelectedWorkKind = _Kind(PluginWorkKinds.Developer);

        Assert.Equal(new[] { "GitHub Issues", "GitHub Pull Requests" }, _Ticked(viewModel));
    }

    [Fact]
    public async Task ATick_StaysTheOperatorsToChange_AfterAWorkKindHasSetIt()
    {
        var viewModel = _ViewModel();
        await viewModel.LoadAsync();
        viewModel.SelectedWorkKind = _Kind(PluginWorkKinds.Developer);

        viewModel.Plugins.Single(row => row.Name == "GitHub Pull Requests").IsSelected = false;
        viewModel.Plugins.Single(row => row.Name == "Invoices").IsSelected = true;
        await viewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "github-issues", "invoices" }, _sentToInstall.Select(request => request.Id));
    }

    [Fact]
    public async Task ChoosingASecondWorkKind_ReplacesTheFirstsTicks_RatherThanAddingToThem()
    {
        var viewModel = _ViewModel();
        await viewModel.LoadAsync();

        viewModel.SelectedWorkKind = _Kind(PluginWorkKinds.Developer);
        viewModel.SelectedWorkKind = _Kind(PluginWorkKinds.Administration);

        Assert.Equal(new[] { "Invoices" }, _Ticked(viewModel));
    }

    /// <summary>
    /// The pre-tick is a suggestion, not a decision already taken: leaving the step — the shell's Skip, or Next
    /// onto the next page — never reaches the confirm command, so a loaded, ticked list installs nothing.
    /// </summary>
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

        await viewModel.ConfirmCommand.ExecuteAsync(null);

        Assert.Contains("1 of 2 installed", viewModel.Summary ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("GitHub Pull Requests (Download failed.)", viewModel.Summary ?? string.Empty, StringComparison.Ordinal);
        await _registrations.Received(1).SaveAsync("github-issues", new PluginRegistration(true, "aaa111"), Arg.Any<CancellationToken>());
        await _registrations.DidNotReceive().SaveAsync("pull-requests", Arg.Any<PluginRegistration>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Criterion 5: an index from before the field exists still drives the step. The chooser has nothing to
    /// suggest, so it offers nothing, and the list is the operator's to tick.
    /// </summary>
    [Fact]
    public async Task AnIndexWithoutTheWorkKindField_LeavesTheStepWorking_WithNothingSuggested()
    {
        var viewModel = _ViewModel(IndexBeforeWorkKindExisted);

        await viewModel.LoadAsync();

        Assert.False(viewModel.HasRecommendations);
        Assert.Equal(new[] { "GitHub Issues" }, viewModel.Plugins.Select(row => row.Name));
        Assert.DoesNotContain(viewModel.Plugins, row => row.IsSelected);
    }

    /// <summary>
    /// Criterion 2, read off the file rather than argued: after the batch, <c>cockpit.json</c> holds enabled
    /// plugins and no trace of the work kind that suggested them. A stored role is what decision 2 of 2026-07-21
    /// forbids — a work kind pre-ticks boxes, it is not something the app later reasons about.
    /// </summary>
    [Fact]
    public async Task AfterTheBatch_TheConfigFileNamesNoWorkKind()
    {
        _batchResult = new PluginProvisionBatchResult(
        [
            new PluginProvisionResult(PluginProvisionOutcome.Installed, "github-issues", "GitHub Issues", null, null, "github-issues", "aaa111"),
            new PluginProvisionResult(PluginProvisionOutcome.Installed, "pull-requests", "GitHub Pull Requests", null, null, "pull-requests", "bbb222"),
        ]);
        var viewModel = new WorkKindStepViewModel(
            _StoreConfig(), _StoreClient(IndexWithWorkKinds), _provisioning, new PluginRegistrationStore(_configFilePath));
        await viewModel.LoadAsync();
        viewModel.SelectedWorkKind = _Kind(PluginWorkKinds.Developer);

        await viewModel.ConfirmCommand.ExecuteAsync(null);

        var written = await File.ReadAllTextAsync(_configFilePath);
        Assert.Contains("github-issues", written, StringComparison.Ordinal);
        Assert.DoesNotContain("workKind", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", written, StringComparison.OrdinalIgnoreCase);
        foreach (var kind in PluginWorkKinds.All)
        {
            Assert.DoesNotContain(kind.Key, written, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(kind.Label, written, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<string> _Ticked(WorkKindStepViewModel viewModel) =>
        viewModel.Plugins.Where(row => row.IsSelected).Select(row => row.Name);

    private static PluginWorkKindOption _Kind(string key) =>
        PluginWorkKinds.All.Single(option => option.Key == key);

    private WorkKindStepViewModel _ViewModel(string indexJson = IndexWithWorkKinds) =>
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
