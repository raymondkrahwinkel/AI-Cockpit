using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugin.Depot.Settings;

// See DepotSettingsControl.cs for why this is a tuple alias, not a new record type.
using DepotSaveResult = (bool Success, string? DuplicateName);

namespace Cockpit.Plugin.Depot.Ui;

/// <summary>
/// One Depot connection's row in the settings view (AC-243): a name, the instance's base URL, and a Sign-in action
/// that drives the host's own OAuth flow for the MCP server this row contributes. No auth fields — Depot has one
/// auth path (OpenIddict OAuth 2.1 + PKCE) and the plugin never holds a credential; see
/// <see cref="DepotConnectionRegistration"/>.
/// <para>
/// AC-499: Sign-in no longer requires the operator to save, close and reopen this dialog first. It is offered as
/// soon as the row's own content is usable (<see cref="_ContentValidationReason"/>) and, on click, saves through
/// <see cref="DepotSettingsControl"/>'s save route (the delegate this row is constructed with) before signing in —
/// the same route Save's own button uses, so the MCP-registry and memory-source sync that live there run every
/// time, not just on an explicit Save. A token is filed under the server's registered name against the URL it was
/// saved with, so the row never signs in under the name it typed: it re-reads <see cref="_settings"/> by this
/// connection's stable <see cref="DepotConnectionRegistration.Id"/> after the save completes and uses whatever
/// name actually made it to storage. Two rows saved under a colliding name never both reach that re-read: the save
/// refuses the whole batch rather than keep one and drop the other (see <see cref="SignInAsync"/>), so a collision
/// is reported before either row's stored state changes.
/// </para>
/// </summary>
internal sealed class DepotConnectionRowControl : UserControl
{
    private readonly ICockpitHost _host;
    private readonly string _id;
    private readonly DepotSettings _settings;
    private readonly Func<DepotSaveResult> _saveAll;
    private string? _storedName;
    private string? _storedUrl;
    private readonly TextBox _name;
    private readonly TextBox _url;
    private readonly TextBlock _authStatus;
    private readonly Button _signIn;
    private bool _isBusy;

    public event Action? RemoveRequested;

    public DepotConnectionRowControl(ICockpitHost host, DepotConnectionRegistration? existing, DepotSettings settings, Func<DepotSaveResult> saveAll)
    {
        _host = host;
        _id = existing?.Id ?? Guid.NewGuid().ToString("n");
        _settings = settings;
        _saveAll = saveAll;
        _storedName = existing?.McpServerName;
        _storedUrl = existing?.Url;

        _name = new TextBox { Text = existing?.Name ?? string.Empty, PlaceholderText = "Name (e.g. Work, Personal)" };
        _url = new TextBox { Text = existing?.Url ?? string.Empty, PlaceholderText = "https://depot.example.com" };
        _authStatus = new TextBlock { FontSize = 11, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };

        _name.TextChanged += (_, _) => _OnFieldsChanged();
        _url.TextChanged += (_, _) => _OnFieldsChanged();

        _signIn = new Button { Content = "Sign in" };
        _signIn.Click += async (_, _) => await SignInAsync().ConfigureAwait(true);

        var remove = new Button { Content = "Remove connection", Margin = new Thickness(0, 4, 0, 0) };
        remove.Click += (_, _) => RemoveRequested?.Invoke();

        var signInRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        signInRow.Children.Add(_signIn);
        signInRow.Children.Add(_authStatus);

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(_Hint("Name"));
        panel.Children.Add(_name);
        panel.Children.Add(_Hint("Instance URL — with or without a trailing /mcp, both work"));
        panel.Children.Add(_url);
        panel.Children.Add(signInRow);
        panel.Children.Add(remove);

        Content = new Border { Padding = new Thickness(0, 8, 0, 12), Child = panel };

        _OnFieldsChanged();
    }

    public string Id => _id;

    /// <summary>A row is blank — and dropped on Save — only when nothing was entered and nothing is already stored for it.</summary>
    public bool IsBlank =>
        string.IsNullOrWhiteSpace(_name.Text)
        && string.IsNullOrWhiteSpace(_url.Text)
        && _storedName is null;

    public DepotConnectionRegistration ToRegistration() => new(
        Id: _id,
        Name: (_name.Text ?? string.Empty).Trim(),
        // AC-499: DepotUrlNormalizer, not a local Trim/TrimEnd — Depot's own docs tell the operator to paste the
        // full endpoint (…/mcp), which this plugin then appends /mcp to again when it builds the MCP contribution.
        // Stripping it here means storage always holds the bare base URL DepotConnectionRegistration.Url promises.
        Url: DepotUrlNormalizer.Normalize(_url.Text));

    /// <summary>
    /// Reads the stored auth state for this row (AC-243/AC-355) — no network, no browser, cheap enough to run for
    /// every row when the dialog opens. A no-op for a row with nothing stored yet, or once the row's name has
    /// drifted from what is stored (see <see cref="_IsUnderStoredIdentity"/>).
    /// </summary>
    public async Task RefreshAuthStateAsync(CancellationToken cancellationToken = default)
    {
        if (!_IsUnderStoredIdentity() || _storedName is not { } storedName)
        {
            return;
        }

        try
        {
            var state = await _host.GetMcpServerAuthStateAsync(storedName, cancellationToken).ConfigureAwait(true);
            _authStatus.Text = state switch
            {
                PluginMcpAuthState.Authorized => "Signed in.",
                PluginMcpAuthState.AuthorizationRequired => "Not signed in.",
                _ => string.Empty,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A status read is informational only; leaving the label blank is better than turning a storage hiccup
            // into a false "not signed in" the operator has to chase.
        }
    }

    /// <summary>
    /// The Sign-in action (AC-499): validates this row's own content, saves the whole list through
    /// <see cref="_saveAll"/>, re-reads what actually landed in storage for this connection's
    /// <see cref="DepotConnectionRegistration.Id"/>, and only then signs in under that name — never under the name
    /// this row's text boxes currently show. Public (like <see cref="RefreshAuthStateAsync"/>) so a test can drive
    /// and await it directly instead of needing a dispatcher-pumped Click.
    /// </summary>
    public async Task SignInAsync()
    {
        if (_isBusy)
        {
            return;
        }

        if (_ContentValidationReason() is { } invalidReason)
        {
            _authStatus.Text = invalidReason;
            return;
        }

        _isBusy = true;
        _signIn.IsEnabled = false;
        _authStatus.Text = "Saving…";
        try
        {
            var (saved, duplicateName) = _saveAll();
            if (!saved)
            {
                // Reachable since the fix that made Save() refuse the whole batch on a same-named collision instead
                // of silently keeping one row and dropping the other — this row could be either one, so it always
                // gets the same honest answer rather than only the one that happened to be dropped finding out.
                _authStatus.Text = duplicateName is { } name
                    ? $"Couldn't save — \"{name}\" is used by another row above. Rename one of them and try again."
                    : "Couldn't save these connections — sign-in was not attempted.";
                return;
            }

            // A save that reports success now always includes this row's own connection — a collision refuses the
            // whole batch (above) rather than silently dropping one side of it, so this row's Id-keyed entry is
            // always in what Save() just wrote. A miss here would mean that invariant broke, not a name collision.
            var stored = _settings.Connections.FirstOrDefault(connection => string.Equals(connection.Id, _id, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Save reported success but connection '{_id}' is missing from storage.");

            _storedName = stored.McpServerName;
            _storedUrl = stored.Url;

            _authStatus.Text = "Signing in…";
            var outcome = await _host.SignInMcpServerAsync(stored.McpServerName).ConfigureAwait(true);
            // Unreachable names the address the plugin actually tried — the outcome enum itself carries no detail
            // (PluginMcpSignInOutcome's own doc comment: "a network/store failure", nothing more specific), but this
            // row already knows which URL it dialed, so the message says at least that much instead of leaving the
            // operator to guess whether the URL, the network or something else was the problem.
            _authStatus.Text = outcome switch
            {
                PluginMcpSignInOutcome.Authorized => "Signed in.",
                PluginMcpSignInOutcome.Declined => "Sign-in didn't complete — see the log, or try again.",
                PluginMcpSignInOutcome.Unreachable => $"Couldn't reach {stored.Url}/mcp to sign in. Check the address and try again.",
                _ => "Sign-in isn't available for this connection right now.",
            };
        }
        finally
        {
            _isBusy = false;
            // Only the button's enabled state, never the status text: a field the operator edited during the
            // save/sign-in round trip must not let this overwrite the outcome that round trip just produced with
            // a re-derived validation message.
            _signIn.IsEnabled = _ContentValidationReason() is null && !_isBusy;
        }
    }

    private void _OnFieldsChanged()
    {
        var reason = _ContentValidationReason();
        _signIn.IsEnabled = reason is null && !_isBusy;
        if (reason is not null)
        {
            _authStatus.Text = reason;
        }
    }

    /// <summary>
    /// Why Sign-in is unavailable, or <see langword="null"/> when the row has enough to save and sign in with — a
    /// disabled control with no reason is a puzzle. Content-only (name present, URL a usable absolute http(s)
    /// address): since AC-499, whether the row already matches what is stored no longer gates the button, because
    /// clicking it saves first.
    /// </summary>
    private string? _ContentValidationReason()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            return "Enter a name first — signing in needs one to file the token under.";
        }

        if (!_IsUsableHttpUrl(_url.Text))
        {
            return "Enter a usable address first — signing in needs somewhere to authorize against.";
        }

        return null;
    }

    private static bool _IsUsableHttpUrl(string? text) =>
        Uri.TryCreate((text ?? string.Empty).Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Whether this row still stands for the connection the store knows by <see cref="_storedName"/> and
    /// <see cref="_storedUrl"/> — used only by <see cref="RefreshAuthStateAsync"/>'s passive status read now (AC-499
    /// moved Sign-in's own gate to <see cref="_ContentValidationReason"/>), so asking the host about auth state
    /// under a name that has since drifted in the text boxes cannot report a stale "signed in".
    /// </summary>
    private bool _IsUnderStoredIdentity() =>
        _storedName is not null
        && _storedUrl is not null
        && string.Equals($"Depot: {(_name.Text ?? string.Empty).Trim()}", _storedName, StringComparison.Ordinal)
        && string.Equals(DepotUrlNormalizer.Normalize(_url.Text), _storedUrl, StringComparison.Ordinal);

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
