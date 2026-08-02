using Avalonia.Controls;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Views.Onboarding;

// The wizard's first page (AC-509 criterion 4): what this app is, in the two things that matter — the
// guarantee and the independence — rather than a feature list.
internal sealed class WelcomeStep : IFirstRunWizardStep, ISingletonService
{
    public int Order => 0;

    public string Title => "What this is";

    public bool IsSkipped => false;

    public Control BuildContent() => new WelcomeStepView();
}
