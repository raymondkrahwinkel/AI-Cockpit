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

// One Depot connection's row in the settings view (AC-243): a name, the instance's base URL, and a Sign-in action
// that drives the host's own OAuth flow for the MCP server this row contributes. No auth fields — Depot has one
// auth path (OpenIddict OAuth 2.1 + PKCE) and the plugin never holds a credential; see
// `DepotConnectionRegistration`.
//
// AC-499: Sign-in no longer requires the operator to save, close and reopen this dialog first. It is offered as
// soon as the row's own content is usable (`_ContentValidationReason`) and, on click, saves through
// `DepotSettingsControl`'s save route (the delegate this row is constructed with) before signing in —
// the same route Save's own button uses, so the MCP-registry and memory-source sync that live there run every
// time, not just on an explicit Save. A token is filed under the server's registered name against the URL it was
// saved with, so the row never signs in under the name it typed: it re-reads `_settings` by this
// connection's stable `DepotConnectionRegistration.Id` after the save completes and uses whatever
// name actually made it to storage. Two rows saved under a colliding name never both reach that re-read: the save
// refuses the whole batch rather than keep one and drop the other (see `SignInAsync`), so a collision
// is reported before either row's stored state changes.
internal sealed class DepotConnectionRowControl : UserControl
{
    // AC-499 UX pass: the operator has to know a click opens a browser before they click it, without a second
    // standing line beside every row (Raymond had the previous one — "signing in saves first" — removed for being
    // exactly that: a line shown on every row regardless of whether there was anything to say). The row's own
    // status slot already carries exactly one relevant sentence per state — the blocked-reason, "Saving…",
    // "Signing in…", an outcome — so the moment the row is enabled but nothing has happened yet is simply the one
    // state that slot said nothing useful in. This message fills it there instead of adding a new line.
    private const string ReadyToSignInMessage = "Opens Depot's sign-in page in your browser.";

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
        // VerticalAlignment.Center on both this label and the button below (idiom also used by SettingsHelpRow and
        // PluginToolbarHost's own icon+text row): a horizontal StackPanel stretches its children to the row's full
        // height by default, which would otherwise top-align this text against a Sign-in button whose own content
        // stays centered inside whatever height it is stretched to — and, since the row's height tracks the taller
        // child, would also visibly grow/shrink the button itself whenever this label's status text wraps to a
        // second line. Centering both keeps the button a fixed, single-line size and puts the label's text — one
        // line or wrapped — in line with the button's own centered content instead of jumping with it.
        _authStatus = new TextBlock { FontSize = 11, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

        _name.TextChanged += (_, _) => _OnFieldsChanged();
        _url.TextChanged += (_, _) => _OnFieldsChanged();

        _signIn = new Button { Content = "Sign in", VerticalAlignment = VerticalAlignment.Center };
        // A free extra, not the primary notice: _authStatus's own text (below/_OnFieldsChanged) is what actually
        // tells the operator a click opens a browser before they click — this tooltip just repeats it on hover,
        // same idiom as SettingsHelpRow's "?" hint and the other ToolTip.SetTip calls throughout this codebase.
        ToolTip.SetTip(_signIn, "Signing in opens Depot's sign-in page in your default browser.");
        _signIn.Click += async (_, _) => await SignInAsync().ConfigureAwait(true);

        var remove = new Button { Content = "Remove connection", Margin = new Thickness(0, 4, 0, 0) };
        remove.Click += (_, _) => RemoveRequested?.Invoke();

        // A Grid, not the StackPanel this replaced: a horizontal StackPanel measures every child with unbounded
        // width along its own orientation, so _authStatus's TextWrapping.Wrap never actually had a width to wrap
        // against — a long outcome message (e.g. the Unreachable case naming the dialed URL) ran off the row
        // instead of wrapping, cut off at the panel's edge rather than visible on a second line. "Auto,*" bounds
        // the text column to whatever width is left once the fixed-size button takes its own — same fixed|flexible
        // idiom SettingsHelpRow's "*,Auto" input+hint row uses, mirrored because the fixed column is on the left
        // here. Margin on the TextBlock replaces the StackPanel's Spacing, same idiom SettingsHelpRow uses for its
        // own "?" hint.
        var signInRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        _authStatus.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_signIn, 0);
        Grid.SetColumn(_authStatus, 1);
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

    // A row is blank — and dropped on Save — only when nothing was entered and nothing is already stored for it.
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

    // Reads the stored auth state for this row (AC-243/AC-355) — no network, no browser, cheap enough to run for
    // every row when the dialog opens. A no-op for a row with nothing stored yet, or once the row's name has
    // drifted from what is stored (see `_IsUnderStoredIdentity`).
    public async Task RefreshAuthStateAsync(CancellationToken cancellationToken = default)
    {
        if (!_IsUnderStoredIdentity() || _storedName is not { } storedName)
        {
            return;
        }

        try
        {
            var state = await _host.GetMcpServerAuthStateAsync(storedName, cancellationToken).ConfigureAwait(true);
            // AuthorizationRequired and Unknown both land the row in the same "enabled, not confirmed signed in"
            // state Sign-in's own UX message is for — Depot connections are always OAuth (see this class's own
            // remarks), so Unknown here means a stale/unread registration, not "sign-in does not apply".
            _authStatus.Text = state switch
            {
                PluginMcpAuthState.Authorized => "Signed in.",
                _ => ReadyToSignInMessage,
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

    // The Sign-in action (AC-499): validates this row's own content, saves the whole list through
    // `_saveAll`, re-reads what actually landed in storage for this connection's
    // `DepotConnectionRegistration.Id`, and only then signs in under that name — never under the name
    // this row's text boxes currently show. Public (like `RefreshAuthStateAsync`) so a test can drive
    // and await it directly instead of needing a dispatcher-pumped Click.
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
            var (saved, failureReason) = _saveAll();
            if (!saved)
            {
                // Reachable since the fix that made Save() refuse the whole batch on a name or URL collision instead
                // of silently keeping one row and dropping the other — this row could be either one, so it always
                // gets the same honest answer rather than only the one that happened to be dropped finding out.
                _authStatus.Text = failureReason is { } reason
                    ? $"Couldn't save — {reason}"
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
        else if (!_isBusy)
        {
            // Only while idle: a field edited mid save/sign-in round trip (Saving…/Signing in…) must not stomp that
            // progress text, the same invariant SignInAsync's own finally block protects for the outcome that
            // follows it — see its comment for why that one never re-derives a message here either.
            _authStatus.Text = ReadyToSignInMessage;
        }
    }

    // Why Sign-in is unavailable, or `null` when the row has enough to save and sign in with — a
    // disabled control with no reason is a puzzle. Content-only (name present, URL a usable absolute http(s)
    // address): since AC-499, whether the row already matches what is stored no longer gates the button, because
    // clicking it saves first.
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

    // Whether this row still stands for the connection the store knows by `_storedName` and
    // `_storedUrl` — used only by `RefreshAuthStateAsync`'s passive status read now (AC-499
    // moved Sign-in's own gate to `_ContentValidationReason`), so asking the host about auth state
    // under a name that has since drifted in the text boxes cannot report a stale "signed in".
    private bool _IsUnderStoredIdentity() =>
        _storedName is not null
        && _storedUrl is not null
        && string.Equals($"Depot: {(_name.Text ?? string.Empty).Trim()}", _storedName, StringComparison.Ordinal)
        && string.Equals(DepotUrlNormalizer.Normalize(_url.Text), _storedUrl, StringComparison.Ordinal);

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
