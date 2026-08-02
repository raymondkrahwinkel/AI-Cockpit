using Avalonia.Controls;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.App.Views.Onboarding;

/// <summary>
/// The wizard's provider page (AC-510[b]): what AI providers this host already has, observed where possible, and
/// a way to add more — while staying honest that Ollama and LM Studio (core, no install) already work on any
/// network. Order 20 leaves room below it for AC-511's work-type step, whichever order that lands on, the same
/// way <see cref="WelcomeStep"/> leaves room above at 0.
/// </summary>
internal sealed class ProviderStep(
    IPluginStoreConfigStore storeConfigStore,
    IPluginStoreClient storeClient,
    IPluginProvisioningService provisioningService,
    PluginBootstrap bootstrap) : IFirstRunWizardStep, ISingletonService
{
    public int Order => 20;

    public string Title => "AI providers";

    public bool IsSkipped => false;

    public Control BuildContent() => new ProviderStepView
    {
        DataContext = new ProviderStepViewModel(storeConfigStore, storeClient, provisioningService, bootstrap),
    };
}
