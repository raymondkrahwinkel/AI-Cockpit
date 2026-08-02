namespace Cockpit.Core.Abstractions;

// The first-run wizard's current content version (AC-509). Bump this when new wizard content should reach an
// operator who already completed an earlier version — `IFirstRunWizardStateStore` stores whichever
// version an install last completed, so a future comparison against this constant is what would decide that.
public static class FirstRunWizardVersion
{
    public const int Current = 1;
}
