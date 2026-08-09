using Avalonia.Controls;

namespace Cockpit.App.Views.Onboarding;

/// <summary>
/// One page of the first-run wizard (AC-509). The shell walks Back/Next/Skip across whatever steps are
/// registered (auto-discovered the same way a plugin registers, via <c>ISingletonService</c>), so a step can be
/// added — <c>AC-510</c>'s provider picker, <c>AC-511</c>'s work-type step — without the shell itself changing.
/// A step left out entirely, like the Depot step <c>AC-540</c> until it lands, still claims its place in the
/// step bar (<c>FirstRunWizardViewModel.EpicPlan</c>) — shown dim rather than simply missing.
/// </summary>
public interface IFirstRunWizardStep
{
    /// <summary>Where this step sits in the step bar and in the Back/Next sequence — lower goes first.</summary>
    int Order { get; }

    /// <summary>The step bar's own label for this step, e.g. "Your account".</summary>
    string Title { get; }

    /// <summary>
    /// Whether this step has nothing to offer right now (e.g. AC-511 when settings were already carried over) —
    /// shown struck through in the step bar and stepped over by Back/Next, rather than removed from it: a skip is
    /// something the operator can see happened, not a silently shorter wizard.
    /// </summary>
    bool IsSkipped { get; }

    /// <summary>This step's own content, built once when the wizard opens.</summary>
    Control BuildContent();
}
