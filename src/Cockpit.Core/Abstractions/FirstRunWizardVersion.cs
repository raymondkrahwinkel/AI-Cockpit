namespace Cockpit.Core.Abstractions;

/// <summary>
/// The first-run wizard's current content version (AC-509). Bump this when new wizard content should reach an
/// operator who already completed an earlier version — <see cref="IFirstRunWizardStateStore"/> stores whichever
/// version an install last completed, so a future comparison against this constant is what would decide that.
/// </summary>
public static class FirstRunWizardVersion
{
    public const int Current = 1;
}
