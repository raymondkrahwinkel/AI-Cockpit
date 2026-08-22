namespace Cockpit.Core.Abstractions;

/// <summary>
/// Shows the first-run wizard on demand (AC-508). It runs once on a fresh install, but the explanatory screen must
/// stay reachable afterwards — a one-off screen gone after a click is content never delivered (AC-509 criterion 3).
/// The Help menu is where the operator looks for it (AC-512); this interface is the seam so the main view and the wizard's own window/completion flag don't need to know how the other is built.
/// </summary>
public interface IFirstRunWizard
{
    /// <summary>
    /// Opens the wizard and returns when the operator has finished or dismissed it. Running it again does not
    /// undo anything that an earlier run installed; it starts the same steps over with the current state.
    /// </summary>
    Task ShowAsync(CancellationToken cancellationToken = default);
}
