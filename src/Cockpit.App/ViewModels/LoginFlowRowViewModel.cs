using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.ViewModels;

// AC-713: a running `ILoginFlow`, rendered inline wherever it was started — one place a login ever plays out.
public sealed partial class LoginFlowRowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly ILoginFlow _flow;
    private readonly CancellationTokenSource _cts = new();

    [ObservableProperty]
    private string _message = "Starting…";

    [ObservableProperty]
    private Uri? _linkToOpen;

    [ObservableProperty]
    private bool _awaitsInput;

    [ObservableProperty]
    private string _codeInput = string.Empty;

    [ObservableProperty]
    private bool _isSubmitting;

    [ObservableProperty]
    private bool _isCompleted;

    [ObservableProperty]
    private bool _succeeded;

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasLink => LinkToOpen is not null;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    // Fires once, with the final `Succeeded`, when the flow finishes — a caller's own login-status flag (the
    // New-session dialog's `IsSelectedProfileLoggedIn`, the auth-expiry bar) can flip on this the moment the CLI
    // actually reports success, rather than waiting for its own next poll of a cache this flow just invalidated.
    public Action<bool>? Completed { get; set; }

    public LoginFlowRowViewModel(ILoginFlow flow)
    {
        _flow = flow;
        _ = _RunAsync();
    }

    [RelayCommand(CanExecute = nameof(_CanSubmit))]
    private async Task SubmitAsync()
    {
        IsSubmitting = true;
        try
        {
            await _flow.SubmitAsync(CodeInput.Trim(), _cts.Token);
            AwaitsInput = false;
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private bool _CanSubmit() => !IsSubmitting && !string.IsNullOrWhiteSpace(CodeInput);

    [RelayCommand(CanExecute = nameof(HasLink))]
    private void OpenLink()
    {
        if (LinkToOpen is { } link)
        {
            ExternalLink.TryOpen(link);
        }
    }

    partial void OnCodeInputChanged(string value) => SubmitCommand.NotifyCanExecuteChanged();

    partial void OnIsSubmittingChanged(bool value) => SubmitCommand.NotifyCanExecuteChanged();

    partial void OnLinkToOpenChanged(Uri? value)
    {
        OnPropertyChanged(nameof(HasLink));
        OpenLinkCommand.NotifyCanExecuteChanged();
    }

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    private async Task _RunAsync()
    {
        try
        {
            await foreach (var step in _flow.Steps.WithCancellation(_cts.Token))
            {
                Message = step.Message;
                LinkToOpen = step.LinkToOpen;
                AwaitsInput = step.AwaitsInput;
            }

            var result = await _flow.Completion;
            Succeeded = result.Success;
            ErrorMessage = result.ErrorMessage;
        }
        catch (OperationCanceledException)
        {
            // Disposed before the flow finished — nothing more to show.
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsCompleted = true;
            Completed?.Invoke(Succeeded);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _flow.DisposeAsync();
        _cts.Dispose();
    }
}
