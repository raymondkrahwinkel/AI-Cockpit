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
/// Also owns the OAuth sign-in/sign-out actions for this row (AC-355), through the injected coordinator. A token is
/// filed under this server's stored name, so <see cref="SignInAsync"/> (AC-499) saves the whole dialog through
/// <see cref="_saveAllForSignIn"/> — the same route the Save button uses — before it authorizes. Sign-in is
/// offered as soon as the row is itself valid, with no manual save first. Authorization still targets the
/// URL/auth as currently typed — fixing a wrong authority and then signing in uses what is on screen. Sign-out is
/// narrower: it only withdraws whatever the store already has, so it needs no save of its own (see
/// <see cref="SignOutAsync"/>).
/// </para>
/// <para>
/// A row is identified by <see cref="_id"/> (AC-403), not by the name in its own text box. That is what makes a
/// rename safe: the name is edited, saved and re-read, while the id it is filed under never moves, so a sign-out on
/// one row can no longer act on what a rename has since handed to another. It also makes
/// <see cref="McpServersViewModel"/>'s post-save resync an identity match rather than the list-position match it had
/// to be while a name was the only handle a row had.
/// </para>
/// </summary>
public partial class EditableMcpServerViewModel : ViewModelBase
{
    private readonly IMcpOAuthCoordinator? _oauthCoordinator;

    /// <summary>
    /// Saves the whole dialog's edited list and returns what the store actually reports back afterward (AC-499) —
    /// the route <see cref="SignInAsync"/> uses so a sign-in never has to wait for a manual save. Owned by this
    /// row's <see cref="McpServersViewModel"/>; null for a row built without one (design-time, or a test that
    /// constructs a row directly), in which case a sign-in has nowhere to save and does nothing.
    /// </summary>
    private readonly Func<Task<IReadOnlyList<McpServerConfig>?>>? _saveAllForSignIn;

    /// <summary>
    /// Whether some other row in the same dialog has a sign-in/sign-out in flight — null for a row built without an
    /// owning <see cref="McpServersViewModel"/> (design-time, or a test that constructs a row directly), in which
    /// case only this row's own <see cref="IsAuthBusy"/> gates it. A dialog-wide save races itself otherwise: a
    /// second row's own save-then-authorize can overwrite the snapshot the first row is mid-resync against.
    /// </summary>
    private readonly Func<bool>? _isDialogBusy;

    /// <summary>
    /// Cancelled once the owning dialog is going away (Save, Cancel, or its own close button) — see
    /// <see cref="McpServersViewModel"/>. Passed to <see cref="IMcpOAuthCoordinator.AcquireAsync"/> so an
    /// interactive sign-in with nowhere left to land its result is told to stop rather than run on against a view
    /// model nothing shows any more.
    /// </summary>
    private readonly CancellationToken _dialogClosing;

    /// <summary>
    /// This row's stable id (AC-403) — what its token is filed under, and what <see cref="McpServersViewModel"/>
    /// finds it by in the reloaded list after a save. Fixed for the row's whole life, including across renames and
    /// including for a row that has not been saved yet: an id minted when the row was added is the id that row will
    /// still have once it is.
    /// </summary>
    internal string Id { get; }

    /// <summary>
    /// Whether the store actually holds this row. False for one the operator has just added and not yet saved —
    /// nothing can be filed for a server the store has never seen, so there is no standing to report and nothing to
    /// withdraw. Set by <see cref="ResyncAfterDialogSaveAsync"/> after every dialog-wide save.
    /// </summary>
    private bool _isPersisted;

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

    /// <summary>Space-separated OAuth scopes that override what the SDK would otherwise derive (AC-505).</summary>
    [ObservableProperty]
    private string _oAuthScopes;

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
    /// a storage read on every keystroke. The URL is also half of Sign in's own validity gate (AC-499), so its
    /// availability needs the same re-check a rename gets below.
    /// </summary>
    partial void OnUrlChanged(string value)
    {
        AuthState = null;
        AuthMessage = string.Empty;
        OnPropertyChanged(nameof(SignInUnavailableReason));
        SignInCommand.NotifyCanExecuteChanged();
    }

    // Sign-in's own validity gate covers Name (via IsValid), so a rename re-checks just that button. Sign-out no
    // longer depends on the typed name at all (AC-499, see SignOutAsync) — it acts on the name this row was last
    // actually saved under, so a rename in progress does not touch its availability.
    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(SignInUnavailableReason));
        SignInCommand.NotifyCanExecuteChanged();
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

    partial void OnIsAuthBusyChanged(bool value) => NotifyDialogBusyChanged();

    /// <summary>
    /// Re-checks Sign in/Sign out after a busy change — this row's own, or (called by <see cref="McpServersViewModel"/>)
    /// another row's, since busy is dialog-wide (AC-499 review fix, finding 6).
    /// </summary>
    internal void NotifyDialogBusyChanged()
    {
        SignInCommand.NotifyCanExecuteChanged();
        SignOutCommand.NotifyCanExecuteChanged();
    }

    /// <param name="isPersisted">
    /// Whether <paramref name="server"/> came from the store. False for a row the operator has just added, whose name
    /// is a placeholder they are about to replace — see <see cref="_isPersisted"/>.
    /// </param>
    /// <param name="saveAllForSignIn">See <see cref="_saveAllForSignIn"/>.</param>
    /// <param name="isDialogBusy">See <see cref="_isDialogBusy"/>.</param>
    /// <param name="dialogClosing">See <see cref="_dialogClosing"/>.</param>
    public EditableMcpServerViewModel(
        McpServerConfig server,
        IMcpOAuthCoordinator? oauthCoordinator = null,
        bool isPersisted = true,
        Func<Task<IReadOnlyList<McpServerConfig>?>>? saveAllForSignIn = null,
        Func<bool>? isDialogBusy = null,
        CancellationToken dialogClosing = default)
    {
        _oauthCoordinator = oauthCoordinator;
        _saveAllForSignIn = saveAllForSignIn;
        _isDialogBusy = isDialogBusy;
        _dialogClosing = dialogClosing;
        // IdentityKey rather than Id: a row read from a config written before ids existed has none of its own, and
        // the key it derives to is the one its already-stored token answers to. A row built by hand with neither
        // (a test, a design-time view) lands on the same derivation, so there is never an empty id in play.
        Id = server.IdentityKey;
        _isPersisted = isPersisted;
        _name = server.Name;
        _transport = server.Transport;
        _command = server.Command ?? string.Empty;
        _args = string.Join(Environment.NewLine, server.Args);
        _url = server.Url ?? string.Empty;
        _auth = server.Auth;
        _apiKey = server.ApiKey ?? string.Empty;
        _oAuthAuthority = server.OAuthAuthority ?? string.Empty;
        _oAuthClientId = server.OAuthClientId ?? string.Empty;
        _oAuthScopes = server.OAuthScopes ?? string.Empty;
        _enabled = server.Enabled;
        _selectedScope = McpServerScopeOption.For(server.Scope);

        foreach (var header in server.Headers)
        {
            Headers.Add(new McpHeaderRowViewModel(header.Name, header.Value));
        }
    }

    /// <summary>
    /// Rebuilds an immutable config from the current edits, keeping only the fields the chosen transport/auth use.
    /// This is both what a save writes and what a sign-in, sign-out or status read is about: the URL and auth are the
    /// typed ones on purpose — an operator correcting a wrong authority and then signing in should get what they just
    /// typed — while <see cref="Id"/> is not editable at all, which is what keeps the credential pointed at this row
    /// no matter what the name box says (AC-403).
    /// </summary>
    public McpServerConfig ToConfig() => new()
    {
        Id = Id,
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
        OAuthScopes = IsOAuthAuth && !string.IsNullOrWhiteSpace(OAuthScopes) ? OAuthScopes.Trim() : null,
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
    /// not OAuth, or for a row the store has never held: nothing can be filed for a server that was never saved, so
    /// there is no standing to report and <see cref="ShowAuthStatus"/> stays false.
    /// </summary>
    public async Task RefreshAuthStateAsync(CancellationToken cancellationToken = default)
    {
        if (_oauthCoordinator is null || !IsOAuthAuth || !_isPersisted)
        {
            return;
        }

        try
        {
            AuthState = await _oauthCoordinator.GetStateAsync(ToConfig(), cancellationToken).ConfigureAwait(true);
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
    /// Resynced after every dialog-wide save (AC-499 review fix, finding 1), for every row in the dialog — not just
    /// the one that asked for the save. <paramref name="persisted"/> is what <see cref="McpServersViewModel"/>
    /// found at this row's own position in the reloaded list, or null if the list came back shorter than the
    /// dialog holds. Re-reads the auth status rather than keeping the old <see cref="AuthState"/>, because the save
    /// is what turns an unsaved row into one that can have a standing at all, and because the sign-in another row
    /// just made can have landed since this row last looked.
    /// </summary>
    internal async Task ResyncAfterDialogSaveAsync(McpServerConfig? persisted, CancellationToken cancellationToken = default)
    {
        _isPersisted = persisted is not null;
        if (persisted is null)
        {
            AuthState = null;
            AuthMessage = string.Empty;
        }

        await RefreshAuthStateAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Why Sign in is unavailable, or empty when it is offered — a disabled button with no reason is a puzzle.
    /// Sign out needs no equivalent message: it disables itself simply because nothing is signed in, which the
    /// status text next to it already says.</summary>
    public string SignInUnavailableReason =>
        !IsOAuthAuth || IsValid ? string.Empty : "Enter a name and a URL first — signing in needs both.";

    private bool CanSignIn =>
        _oauthCoordinator is not null && IsOAuthAuth && !IsAuthBusy && !(_isDialogBusy?.Invoke() ?? false) && IsValid;

    /// <summary>
    /// The operator's own "log me in" act (AC-355/AC-499) — the one call site anywhere that asks interactively, and
    /// so the only one allowed to open a browser. A sign-in used to require a manual save first, because a token is
    /// filed under the name the server is stored as; that requirement stayed, but the manual step did not need to.
    /// This now saves the whole dialog through <see cref="_saveAllForSignIn"/> — the same route the Save button
    /// uses, and which resyncs every row before it returns (see the class remarks) — then authorizes. Since AC-403
    /// the credential lands under <see cref="Id"/> rather than under a name, so what the save has to establish is
    /// only that this row is in the store at all. Authorization still targets the URL/auth as currently typed.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        // The command is gated on CanSignIn, but AsyncRelayCommand.ExecuteAsync does not consult CanExecute — so the
        // same checks are required here too. A row with no save route (no owning dialog) cannot sign in at all.
        if (_oauthCoordinator is null || _saveAllForSignIn is null || !IsValid)
        {
            return;
        }

        IsAuthBusy = true;
        AuthMessage = string.Empty;
        try
        {
            if (await _saveAllForSignIn().ConfigureAwait(true) is null)
            {
                // The save route already put why on the dialog's own status message — which may say "nothing was
                // saved" or "saved, but the store didn't confirm it" (AC-499 review fix, finding 2); either way,
                // authorizing on top of it would risk filing a token under a name the store never actually took.
                AuthMessage = "Couldn't finish signing in — see the message below.";
                return;
            }

            // The save just resynced every row in the dialog, this one included, against the id it found for it in
            // what the store reported back. Not persisted here means this row's id was not in that list, which
            // should not happen for an uncontested save but is reported rather than guessed past.
            if (!_isPersisted)
            {
                AuthMessage = "Saved, but this server isn't in the store — try again.";
                return;
            }

            var access = await _oauthCoordinator.AcquireAsync(ToConfig(), interactive: true, _dialogClosing).ConfigureAwait(true);
            AuthState = access.State;
            if (access.State != McpAuthState.Authorized)
            {
                // AcquireAsync answers with a state and how far the sign-in got, never the failure detail (and never
                // a token to leak, Iron Law #8) — so these stay fixed lines rather than any exception text. What the
                // stage buys is that each one is true: a single safe sentence sent the operator to a browser window
                // on a run where discovery had already refused and no window ever existed (AC-457).
                AuthMessage = access.SignInStage switch
                {
                    McpSignInStage.BrowserRequested => "Cockpit handed the sign-in to your browser, and nothing came back. Try again, or see the log.",

                    // Deliberately not "the server refused": a sign-in that succeeds and issues a credential with
                    // less life left than the margin lands here too, and on that run nothing refused anything.
                    McpSignInStage.AuthorizationReturned => "The browser came back, but no usable credential came with it — see the log.",

                    // Anything the stage cannot vouch for lands here, so this wording carries no cause of its own —
                    // a stage that was never recorded must not be allowed to assert one. The referral holds because
                    // the coordinator writes a line for every interactive failure, including the quiet ones.
                    _ => "Sign-in stopped before it reached a browser — see the log.",
                };
            }
        }
        catch (OperationCanceledException)
        {
            // The dialog closed out from under this sign-in — Save, Cancel, or the window's own close button
            // (AC-499 review fix, finding 6). Nothing shows this row any more, so this is a clean stop, not a
            // failure to report.
        }
        catch (Exception)
        {
            // Naming the URL or the OAuth settings as the cause is the same untruth the stages above exist to stop:
            // what reaches here is whatever escaped the save or the coordinator, and this path has no stage to speak
            // from and no log line of its own to point at.
            AuthMessage = "Sign-in failed. Try again.";
        }
        finally
        {
            IsAuthBusy = false;
        }
    }

    private bool CanSignOut =>
        _oauthCoordinator is not null && IsOAuthAuth && !IsAuthBusy && !(_isDialogBusy?.Invoke() ?? false)
        && _isPersisted && AuthState == McpAuthState.Authorized;

    /// <summary>
    /// Withdraws whatever access is held for this server (AC-355) — only offered while there is something to
    /// withdraw. Unlike sign-in, this needs no save first (AC-499): it acts on <see cref="Id"/>, which an unsaved
    /// rename does not touch. Signing out undoes what the store has on file, not the row's pending edits, so gating
    /// it on the typed name matching would refuse a withdrawal the operator is entitled to make. A row the store has
    /// never held has nothing to withdraw. And since AC-403 there is no way left for one row's withdrawal to reach
    /// another row's token: an id is not something a save can hand to somebody else.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSignOut))]
    private async Task SignOutAsync()
    {
        // The command is gated on CanSignOut, but AsyncRelayCommand.ExecuteAsync does not consult CanExecute — so
        // being in the store is required here too rather than assumed.
        if (_oauthCoordinator is null || !_isPersisted)
        {
            return;
        }

        IsAuthBusy = true;
        AuthMessage = string.Empty;
        try
        {
            var config = ToConfig();
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
