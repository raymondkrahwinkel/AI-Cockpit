using Avalonia.Controls;

namespace Cockpit.App.Views.Onboarding;

/// <summary>
/// One page of the first-run wizard (AC-509). The shell walks Back/Next/Skip across registered steps
/// (auto-discovered like a plugin, via <c>ISingletonService</c>). A step left out entirely, like the Depot step
/// <c>AC-540</c> until it lands, still claims its place in the step bar — shown dim rather than simply missing.
/// </summary>
public interface IFirstRunWizardStep
{
    /// <summary>
    /// Where this step sits in the step bar and in the Back/Next sequence — lower goes first.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// The step bar's own label for this step, e.g. "Your account".
    /// </summary>
    string Title { get; }

    /// <summary>
    /// Whether this step has nothing to offer right now (e.g. AC-511 when settings were already carried over) —
    /// shown struck through in the step bar and stepped over by Back/Next, rather than removed from it: a skip is
    /// something the operator can see happened, not a silently shorter wizard.
    /// </summary>
    bool IsSkipped { get; }

    /// <summary>
    /// This step's own content, built once when the wizard opens.
    /// </summary>
    Control BuildContent();
}
