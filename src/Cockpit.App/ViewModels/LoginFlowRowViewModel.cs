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
    private bool _linkOpened;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCodeTimer))]
    private bool _isCompleted;

    [ObservableProperty]
    private bool _succeeded;

    [ObservableProperty]
    private string? _errorMessage;

    // AC-1477: how long the code on screen stays valid, counted down once a second while the flow runs. Empty when the
    // flow gave no expiry; the expired text replaces it at zero.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCodeTimer))]
    private string _codeValidFor = string.Empty;

    public bool HasCodeTimer => CodeValidFor.Length > 0 && !IsCompleted;

    public bool HasLink => LinkToOpen is not null;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    // Fires once, with the final `Succeeded`, when the flow finishes — a caller's own login-status flag (the
    // New-session dialog's `IsSelectedProfileLoggedIn`, the auth-expiry bar) can flip on this the moment the CLI
    // actually reports success, rather than waiting for its own next poll of a cache this flow just invalidated.
    public Action<bool>? Completed { get; set; }

    private readonly TimeProvider _time;
    private DateTimeOffset? _expiresAt;

    public LoginFlowRowViewModel(ILoginFlow flow, TimeProvider? time = null)
    {
        _flow = flow;
        _time = time ?? TimeProvider.System;
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
            LinkOpened = ExternalLink.TryOpen(link);
        }
    }

    partial void OnCodeInputChanged(string value) => SubmitCommand.NotifyCanExecuteChanged();

    partial void OnIsSubmittingChanged(bool value) => SubmitCommand.NotifyCanExecuteChanged();

    partial void OnLinkToOpenChanged(Uri? value)
    {
        OnPropertyChanged(nameof(HasLink));
        OpenLinkCommand.NotifyCanExecuteChanged();
        LinkOpened = false;
    }

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    private async Task _RunAsync()
    {
        try
        {
            await foreach (var step in _flow.Steps.WithCancellation(_cts.Token))
            {
                Message = step.Message;
                // AC-731: sticky — a later step that carries no link (e.g. the CLI's own "paste code" prompt)
                // must not blank out a link the operator has not opened yet.
                if (step.LinkToOpen is not null)
                {
                    LinkToOpen = step.LinkToOpen;
                }
                AwaitsInput = step.AwaitsInput;
                if (step.ExpiresAt is { } expiresAt && _expiresAt != expiresAt)
                {
                    _expiresAt = expiresAt;
                    _ShowCodeValidFor();
                    _ = _CountDownAsync();
                }
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

    private void _ShowCodeValidFor()
    {
        var left = (_expiresAt ?? _time.GetUtcNow()) - _time.GetUtcNow();
        CodeValidFor = left > TimeSpan.Zero
            ? $"code valid for {(int)left.TotalMinutes:00}:{left.Seconds:00}"
            : "The code has expired. Start the sign-in again.";
    }

    private async Task _CountDownAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        try
        {
            while (!IsCompleted && _expiresAt > _time.GetUtcNow() && await timer.WaitForNextTickAsync(_cts.Token))
            {
                _ShowCodeValidFor();
            }

            if (!IsCompleted)
            {
                _ShowCodeValidFor();
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed — nothing more to count.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await _flow.DisposeAsync();
        _cts.Dispose();
    }
}
