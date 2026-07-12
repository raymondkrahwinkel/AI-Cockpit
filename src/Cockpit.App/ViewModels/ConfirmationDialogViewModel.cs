namespace Cockpit.App.ViewModels;

/// <summary>
/// Backs the generic confirmation dialog shown before a destructive action (removing a store, plugin, MCP
/// server, prompt template, …): a <see cref="Title"/>, a <see cref="Message"/> spelling out exactly what will
/// happen, and the label for the confirm button (<see cref="ConfirmLabel"/>, e.g. "Remove"). The dialog returns
/// true only when the operator clicks confirm.
/// </summary>
public sealed class ConfirmationDialogViewModel
{
    public string Title { get; }

    public string Message { get; }

    public string ConfirmLabel { get; }

    // Design-time constructor for the previewer.
    public ConfirmationDialogViewModel()
        : this("Confirm", "Are you sure?", "Confirm")
    {
    }

    public ConfirmationDialogViewModel(string title, string message, string confirmLabel)
    {
        Title = title;
        Message = message;
        ConfirmLabel = confirmLabel;
    }
}
