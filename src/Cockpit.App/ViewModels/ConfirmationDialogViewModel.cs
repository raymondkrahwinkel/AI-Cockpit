namespace Cockpit.App.ViewModels;

// Backs the generic confirmation dialog shown before a destructive action (removing a store, plugin, MCP server, prompt
// template, …): a `Title`, a `Message` spelling out exactly what will happen, and the label for the
// confirm button (`ConfirmLabel`, e.g. "Remove"). The dialog returns true only when the operator clicks confirm.
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
