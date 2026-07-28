using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.App.ViewModels;

/// <summary>
/// A mutable, editable view over an immutable <see cref="McpServerConfig"/> for the MCP-servers dialog
/// (#26). Args are edited as one-per-line text; transport/auth are enum selections that drive which
/// fields the dialog shows. <see cref="ToConfig"/> turns the edits back into a config on save.
/// <para>
/// Also owns the OAuth sign-in/sign-out actions for this row (AC-355), through the injected coordinator. They
/// authorize against the URL and auth as currently typed — fixing a wrong authority and then signing in uses what is
/// on screen — but always under the name this server is stored as, and they are offered only while the two agree
/// (<see cref="IsSignInAvailable"/>).
/// </para>
/// </summary>
public partial class EditableMcpServerViewModel : ViewModelBase
{
    private readonly IMcpOAuthCoordinator? _oauthCoordinator;

    /// <summary>
    /// The name this server is stored under, or <see langword="null"/> for a row that has never been saved. A token
    /// is keyed by server name, so this is the only name a sign-in or a withdrawal may act on — and a row with no
    /// stored name has no token to act on at all, which is why both are withheld there rather than aimed at a guess.
    /// Trimmed, because that is what a save writes: an entry a hand-edit or an older build left with a trailing space
    /// would otherwise never match the typed name and the row would refuse both buttons for good.
    /// </summary>
    private readonly string? _storedUnderName;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private McpTransport _transport;

    /// <summary>Which session worlds this server is exposed to (all / local models only / Claude only).</summary>
    [ObservableProperty]
    private McpServerScopeOption _selectedScope;

    [ObservableProperty]
    private string _command;

    /// <summary>Stdio arguments, one per line.</summary>
    [ObservableProperty]
    private string _args;

    [ObservableProperty]
    private string _url;

    [ObservableProperty]
    private McpServerAuth _auth;

    [ObservableProperty]
    private string _apiKey;

    [ObservableProperty]
    private string _oAuthAuthority;

    [ObservableProperty]
    private string _oAuthClientId;

    [ObservableProperty]
    private bool _enabled;

    /// <summary>
    /// Custom headers sent to an HTTP server alongside whatever <see cref="Auth"/> arranges (AC-354) — for a
    /// server that expects <c>X-Api-Key</c> or another scheme <see cref="McpServerAuth.ApiKey"/> cannot express.
    /// Shown whenever the transport is HTTP, independent of the auth choice: a header can sit next to None,
    /// ApiKey or OAuth just as well.
    /// </summary>
    public ObservableCollection<McpHeaderRowViewModel> Headers { get; } = [];

    /// <summary>
    /// What the cockpit knows about this server's OAuth standing (AC-355) — null until a coordinator has answered
    /// (design-time, or before <see cref="RefreshAuthStateAsync"/> has run) or once the server stops being OAuth
    /// (<see cref="OnAuthChanged"/>/<see cref="OnTransportChanged"/> clear it, so a stale "signed in" never survives
    /// a switch away from OAuth). Only meaningful for <see cref="IsOAuthAuth"/> — a None/ApiKey server needs no
    /// sign-in, which is not worth a badge.
    /// </summary>
    [ObservableProperty]
    private McpAuthState? _authState;

    /// <summary>Whether a sign-in/sign-out is in flight for this row — disables both buttons so a slow browser round-trip cannot be started twice.</summary>
    [ObservableProperty]
    private bool _isAuthBusy;

    /// <summary>
    /// A short, fixed message for the last sign-in/sign-out failure — never the raw exception text (Iron Law #8: an
    /// OAuth failure can carry a fragment of the request/response, and <see cref="IMcpOAuthCoordinator.AcquireAsync"/>
    /// only ever answers with a state anyway, never a token to leak). Empty when there is nothing to report.
    /// </summary>
    [ObservableProperty]
    private string _authMessage = string.Empty;

    /// <summary>Whether the status row/badge is worth showing at all — an OAuth server once its state is known.</summary>
    public bool ShowAuthStatus => IsOAuthAuth && AuthState is not null;

    /// <summary>The list-row/detail badge text; empty when there is nothing to show yet.</summary>
    public string AuthStatusLabel => AuthState switch
    {
        McpAuthState.Authorized => "signed in",
        McpAuthState.AuthorizationRequired => "sign-in needed",
        _ => string.Empty,
    };

    /// <summary>"Sign in again" once a sign-in has succeeded before, "Sign in" otherwise — the same button either way.</summary>
    public string SignInButtonLabel => AuthState == McpAuthState.Authorized ? "Sign in again" : "Sign in";

    public IReadOnlyList<McpTransport> Transports { get; } = Enum.GetValues<McpTransport>();

    public IReadOnlyList<McpServerAuth> AuthModes { get; } = Enum.GetValues<McpServerAuth>();

    public IReadOnlyList<McpServerScopeOption> Scopes { get; } = McpServerScopeOption.All;

    public bool IsStdio => Transport == McpTransport.Stdio;

    public bool IsHttp => Transport == McpTransport.Http;

    public bool IsApiKeyAuth => IsHttp && Auth == McpServerAuth.ApiKey;

    public bool IsOAuthAuth => IsHttp && Auth == McpServerAuth.OAuth;

    /// <summary>A server needs a name plus the fields its transport requires — a command for stdio, a URL for http.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name)
        && (IsStdio ? !string.IsNullOrWhiteSpace(Command) : !string.IsNullOrWhiteSpace(Url));

    partial void OnTransportChanged(McpTransport value)
    {
        OnPropertyChanged(nameof(IsStdio));
        OnPropertyChanged(nameof(IsHttp));
        OnPropertyChanged(nameof(IsApiKeyAuth));
        OnPropertyChanged(nameof(IsOAuthAuth));
        _ResetAuthStatusIfNoLongerOAuth();
    }

    /// <summary>
    /// A held token is bound to the host it was obtained for, so retyping the URL can make "signed in" false without
    /// anything else changing. The badge goes back to unknown rather than keeping a label whose reason has gone —
    /// the same failure as a warning that outlives what it warned about. Not re-read here on purpose: that would put
    /// a storage read on every keystroke.
    /// </summary>
    partial void OnUrlChanged(string value)
    {
        AuthState = null;
        AuthMessage = string.Empty;
    }

    // Renaming needs no badge-clearing of its own: while the typed name differs from the stored one the buttons are
    // simply not offered (IsSignInAvailable), which says the same thing without leaving a token unreachable — the
    // trap in clearing the badge is that Sign out is gated on it. What a rename still breaks is the save, where the
    // registry gets the new name and the token keeps the old; that is storage, not display, and it is AC-403.
    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(IsSignInAvailable));
        OnPropertyChanged(nameof(SignInUnavailableReason));
        SignInCommand.NotifyCanExecuteChanged();
        SignOutCommand.NotifyCanExecuteChanged();
    }

    partial void OnAuthChanged(McpServerAuth value)
    {
        OnPropertyChanged(nameof(IsApiKeyAuth));
        OnPropertyChanged(nameof(IsOAuthAuth));
        _ResetAuthStatusIfNoLongerOAuth();
    }

    // A server that just stopped being OAuth (switched transport or auth mode) must not keep showing whatever it
    // last knew about a sign-in that no longer applies — that would read as "signed in" for a server this edit just
    // turned into a plain API-key one.
    private void _ResetAuthStatusIfNoLongerOAuth()
    {
        if (!IsOAuthAuth)
        {
            AuthState = null;
            AuthMessage = string.Empty;
        }

        OnPropertyChanged(nameof(IsSignInAvailable));
        OnPropertyChanged(nameof(SignInUnavailableReason));

        SignInCommand.NotifyCanExecuteChanged();
        SignOutCommand.NotifyCanExecuteChanged();
    }

    partial void OnAuthStateChanged(McpAuthState? value)
    {
        OnPropertyChanged(nameof(ShowAuthStatus));
        OnPropertyChanged(nameof(AuthStatusLabel));
        OnPropertyChanged(nameof(SignInButtonLabel));
        SignOutCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAuthBusyChanged(bool value)
    {
        SignInCommand.NotifyCanExecuteChanged();
        SignOutCommand.NotifyCanExecuteChanged();
    }

    /// <param name="isPersisted">
    /// Whether <paramref name="server"/> came from the store. False for a row the operator has just added, whose name
    /// is a placeholder they are about to replace — see <see cref="_storedUnderName"/>.
    /// </param>
    public EditableMcpServerViewModel(McpServerConfig server, IMcpOAuthCoordinator? oauthCoordinator = null, bool isPersisted = true)
    {
        _oauthCoordinator = oauthCoordinator;
        _storedUnderName = isPersisted ? server.Name.Trim() : null;
        _name = server.Name;
        _transport = server.Transport;
        _command = server.Command ?? string.Empty;
        _args = string.Join(Environment.NewLine, server.Args);
        _url = server.Url ?? string.Empty;
        _auth = server.Auth;
        _apiKey = server.ApiKey ?? string.Empty;
        _oAuthAuthority = server.OAuthAuthority ?? string.Empty;
        _oAuthClientId = server.OAuthClientId ?? string.Empty;
        _enabled = server.Enabled;
        _selectedScope = McpServerScopeOption.For(server.Scope);

        foreach (var header in server.Headers)
        {
            Headers.Add(new McpHeaderRowViewModel(header.Name, header.Value));
        }
    }

    /// <summary>
    /// What a sign-in, sign-out or status read is about: the edits as they stand, under <paramref name="storedName"/>.
    /// The URL and auth are the typed ones on purpose — an operator correcting a wrong authority and then signing in
    /// should get what they just typed. The name is passed in rather than read here, so there is nowhere left for it
    /// to fall back to a guess: every caller has to hold a stored name before it can ask.
    /// </summary>
    private McpServerConfig _AuthTarget(string storedName) => ToConfig() with { Name = storedName };

    /// <summary>Rebuilds an immutable config from the current edits, keeping only the fields the chosen transport/auth use.</summary>
    public McpServerConfig ToConfig() => new()
    {
        Name = Name.Trim(),
        Transport = Transport,
        Scope = SelectedScope.Scope,
        Command = IsStdio && !string.IsNullOrWhiteSpace(Command) ? Command.Trim() : null,
        Args = IsStdio
            ? Args.Split('\n').Select(arg => arg.Trim()).Where(arg => arg.Length > 0).ToList()
            : [],
        Url = IsHttp && !string.IsNullOrWhiteSpace(Url) ? Url.Trim() : null,
        Auth = IsHttp ? Auth : McpServerAuth.None,
        ApiKey = IsApiKeyAuth && !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey.Trim() : null,
        OAuthAuthority = IsOAuthAuth && !string.IsNullOrWhiteSpace(OAuthAuthority) ? OAuthAuthority.Trim() : null,
        OAuthClientId = IsOAuthAuth && !string.IsNullOrWhiteSpace(OAuthClientId) ? OAuthClientId.Trim() : null,
        Headers = IsHttp
            ? [.. Headers.Select(row => new McpHeader(row.Name.Trim(), row.Value.Trim())).Where(header => header.IsComplete)]
            : [],
        Enabled = Enabled,
    };

    [RelayCommand]
    private void AddHeader() => Headers.Add(new McpHeaderRowViewModel());

    [RelayCommand]
    private void RemoveHeader(McpHeaderRowViewModel row) => Headers.Remove(row);

    /// <summary>
    /// Reads the stored auth state for this server (AC-355) — no network, no browser, so this is cheap enough to
    /// run for every row when the dialog opens. A no-op without a coordinator (design-time), for a server that is
    /// not OAuth, or for a row with no stored name: nothing can be filed under a name the store does not have, so
    /// there is no standing to report and <see cref="ShowAuthStatus"/> stays false.
    /// </summary>
    public async Task RefreshAuthStateAsync(CancellationToken cancellationToken = default)
    {
        if (_oauthCoordinator is null || !IsOAuthAuth || _storedUnderName is not { } storedName)
        {
            return;
        }

        try
        {
            AuthState = await _oauthCoordinator.GetStateAsync(_AuthTarget(storedName), cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A status read is informational only; leaving the badge as "unknown" (no change) is better than
            // turning a storage hiccup into a false "sign-in needed" the operator has to chase.
        }
    }

    /// <summary>
    /// Whether this row still stands for the server the store knows by that name — the condition both sign-in and
    /// sign-out are gated on, and the reason the key can no longer drift.
    /// <para>
    /// A token is filed under a server's name, which the operator may retype at will. Four rounds of review on this
    /// ticket each found the same defect one case further along, because the row was guessing which name its token
    /// was under: the placeholder of a new row, the name at construction, the name at the moment a sign-in happened.
    /// Refusing to act while the row and the store disagree removes the guess instead of improving it — the name is
    /// then always the stored one, because that is the only state in which either button is offered.
    /// </para>
    /// </summary>
    public bool IsSignInAvailable =>
        _storedUnderName is not null && string.Equals(Name.Trim(), _storedUnderName, StringComparison.Ordinal);

    /// <summary>Why the buttons are unavailable, or empty when they are not — a disabled control with no reason is a puzzle.</summary>
    public string SignInUnavailableReason =>
        !IsOAuthAuth || IsSignInAvailable ? string.Empty
        : _storedUnderName is null ? "Save this server first — signing in files the token under its name."
        : "Save the new name first — the sign-in is filed under the name this server is stored as.";

    private bool CanSignIn => IsOAuthAuth && !IsAuthBusy && IsSignInAvailable;

    /// <summary>
    /// The operator's own "log me in" act (AC-355) — the one call site anywhere that asks interactively, and so the
    /// only one allowed to open a browser. Authorizes against the URL/authority as typed, filed under the name this
    /// server is stored as.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        // The command is gated on IsSignInAvailable, but AsyncRelayCommand.ExecuteAsync does not consult CanExecute —
        // so the stored name is required here too rather than assumed.
        if (_oauthCoordinator is null || _storedUnderName is not { } storedName)
        {
            return;
        }

        IsAuthBusy = true;
        AuthMessage = string.Empty;
        try
        {
            var access = await _oauthCoordinator.AcquireAsync(_AuthTarget(storedName), interactive: true).ConfigureAwait(true);
            AuthState = access.State;
            if (access.State != McpAuthState.Authorized)
            {
                // AcquireAsync only ever answers with a state, never the failure detail (and never a token to
                // leak, Iron Law #8) — so this stays a fixed, short line rather than any exception text.
                AuthMessage = "Sign-in did not complete. Check the browser window, then try again.";
            }
        }
        catch (Exception)
        {
            AuthMessage = "Sign-in failed. Check the server's URL and OAuth settings, then try again.";
        }
        finally
        {
            IsAuthBusy = false;
        }
    }

    private bool CanSignOut => IsOAuthAuth && !IsAuthBusy && IsSignInAvailable && AuthState == McpAuthState.Authorized;

    /// <summary>Withdraws whatever access is held for this server (AC-355) — only offered while there is something to withdraw.</summary>
    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private async Task SignOutAsync()
    {
        // The command is gated on IsSignInAvailable, but AsyncRelayCommand.ExecuteAsync does not consult CanExecute —
        // so the stored name is required here too rather than assumed.
        if (_oauthCoordinator is null || _storedUnderName is not { } storedName)
        {
            return;
        }

        IsAuthBusy = true;
        AuthMessage = string.Empty;
        try
        {
            var config = _AuthTarget(storedName);
            await _oauthCoordinator.SignOutAsync(config).ConfigureAwait(true);
            AuthState = await _oauthCoordinator.GetStateAsync(config).ConfigureAwait(true);
        }
        catch (Exception)
        {
            AuthMessage = "Couldn't sign out. Try again.";
        }
        finally
        {
            IsAuthBusy = false;
        }
    }
}
