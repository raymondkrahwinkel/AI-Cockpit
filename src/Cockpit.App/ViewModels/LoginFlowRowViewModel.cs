using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.App.Services;
using Cockpit.Plugins.Abstractions.Sessions;
using System.Text.RegularExpressions;

namespace Cockpit.App.ViewModels;

// AC-713: a running `ILoginFlow`, rendered inline wherever it was started — one place a login ever plays out.
public sealed partial class LoginFlowRowViewModel : ViewModelBase, IAsyncDisposable
{
    private static readonly Regex DeviceCodePattern = new(@"\b[A-Z0-9]{4,5}-[A-Z0-9]{4,5}\b", RegexOptions.Compiled);

    private readonly ILoginFlow _flow;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public Func<Task>? RestartSignIn { get; init; }

    public bool CanRestart => RestartSignIn is not null;

    public bool IsSecretInput { get; init; }

    public char InputPasswordChar => IsSecretInput ? '\u25cf' : '\0';

    public string InputPlaceholder { get; init; } = "Paste the code here";

    public bool ShowCodexDeviceHint { get; init; } = true;

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private async Task RestartAsync()
    {
        await DisposeAsync();
        if (RestartSignIn is { } restart)
        {
            await restart();
        }
    }

    [ObservableProperty]
    private string _message = "Starting…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeviceCode))]
    private string _deviceCode = string.Empty;

    public bool HasDeviceCode => DeviceCode.Length > 0;

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
            var input = CodeInput.Trim();
            if (IsSecretInput)
            {
                CodeInput = string.Empty;
            }
            await _flow.SubmitAsync(input, _cts.Token);
            AwaitsInput = false;
        }
        catch (Exception) when (IsSecretInput)
        {
            ErrorMessage = "The token could not be submitted. Try again.";
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
                if (!step.AwaitsInput && DeviceCodePattern.Match(step.Message) is { Success: true } code)
                {
                    DeviceCode = code.Value;
                }
                // AC-731: sticky — a later step that carries no link (e.g. the CLI's own "paste code" prompt)
                // must not blank out a link the operator has not opened yet.
                if (step.LinkToOpen is not null)
                {
                    LinkToOpen = step.LinkToOpen;
                }
                AwaitsInput = step.AwaitsInput;
                if (step.ExpiresAt is { } expiresAt)
                {
                    var counting = _expiresAt is not null;
                    _expiresAt = expiresAt;
                    _ShowCodeValidFor();
                    if (!counting)
                    {
                        _ = _CountDownAsync(_cts.Token);
                    }
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
            ErrorMessage = IsSecretInput ? "GitHub sign-in could not finish. Try again." : ex.Message;
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

    private async Task _CountDownAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
        try
        {
            while (!IsCompleted && _expiresAt > _time.GetUtcNow() && await timer.WaitForNextTickAsync(cancellationToken))
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cts.CancelAsync();
        await _flow.DisposeAsync();
        _cts.Dispose();
    }
}
