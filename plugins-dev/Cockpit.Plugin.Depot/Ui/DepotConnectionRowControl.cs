using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugin.Depot.Model;

namespace Cockpit.Plugin.Depot.Ui;

/// <summary>
/// One Depot connection's row in the settings view (AC-243): a name, the instance's base URL, and a Sign-in action
/// that drives the host's own OAuth flow for the MCP server this row contributes. No auth fields — Depot has one
/// auth path (OpenIddict OAuth 2.1 + PKCE) and the plugin never holds a credential; see
/// <see cref="DepotConnectionRegistration"/>.
/// <para>
/// Sign-in is only offered once the row's current name <em>and</em> URL both match what is actually registered
/// (<see cref="_storedName"/>/<see cref="_storedUrl"/>) — the same rule <c>EditableMcpServerViewModel.IsSignInAvailable</c>
/// enforces for the host's own MCP-servers dialog, for the same reason: a token is filed under the server's
/// registered name against the URL/authority as saved, and signing in against a URL edited but not yet saved would
/// authorize a different issuer than the one the connection is about to be saved as pointing at.
/// </para>
/// </summary>
internal sealed class DepotConnectionRowControl : UserControl
{
    private readonly ICockpitHost _host;
    private readonly string _id;
    private readonly string? _storedName;
    private readonly string? _storedUrl;
    private readonly TextBox _name;
    private readonly TextBox _url;
    private readonly TextBlock _authStatus;
    private readonly Button _signIn;
    private bool _isBusy;

    public event Action? RemoveRequested;

    public DepotConnectionRowControl(ICockpitHost host, DepotConnectionRegistration? existing)
    {
        _host = host;
        _id = existing?.Id ?? Guid.NewGuid().ToString("n");
        _storedName = existing?.McpServerName;
        _storedUrl = existing?.Url;

        _name = new TextBox { Text = existing?.Name ?? string.Empty, PlaceholderText = "Name (e.g. Work, Personal)" };
        _url = new TextBox { Text = existing?.Url ?? string.Empty, PlaceholderText = "https://depot.example.com" };
        _authStatus = new TextBlock { FontSize = 11, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };

        _name.TextChanged += (_, _) => _OnFieldsChanged();
        _url.TextChanged += (_, _) => _OnFieldsChanged();

        _signIn = new Button { Content = "Sign in" };
        _signIn.Click += async (_, _) => await _SignInAsync().ConfigureAwait(true);

        var remove = new Button { Content = "Remove connection", Margin = new Thickness(0, 4, 0, 0) };
        remove.Click += (_, _) => RemoveRequested?.Invoke();

        var signInRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        signInRow.Children.Add(_signIn);
        signInRow.Children.Add(_authStatus);

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(_Hint("Name"));
        panel.Children.Add(_name);
        panel.Children.Add(_Hint("Instance URL"));
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
        Url: (_url.Text ?? string.Empty).Trim().TrimEnd('/'));

    /// <summary>
    /// Reads the stored auth state for this row (AC-243/AC-355) — no network, no browser, cheap enough to run for
    /// every row when the dialog opens. A no-op for a row with nothing stored yet, or once the row's name has
    /// drifted from what is stored (see <see cref="_IsSignInAvailable"/>).
    /// </summary>
    public async Task RefreshAuthStateAsync(CancellationToken cancellationToken = default)
    {
        if (!_IsSignInAvailable() || _storedName is not { } storedName)
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

    private async Task _SignInAsync()
    {
        if (_isBusy || !_IsSignInAvailable() || _storedName is not { } storedName)
        {
            return;
        }

        _isBusy = true;
        _signIn.IsEnabled = false;
        _authStatus.Text = "Signing in…";
        try
        {
            var outcome = await _host.SignInMcpServerAsync(storedName).ConfigureAwait(true);
            _authStatus.Text = outcome switch
            {
                PluginMcpSignInOutcome.Authorized => "Signed in.",
                PluginMcpSignInOutcome.Declined => "Sign-in didn't complete — see the log, or try again.",
                PluginMcpSignInOutcome.Unreachable => "Couldn't reach the sign-in flow. Try again.",
                _ => "Sign-in isn't available for this connection yet — save it first.",
            };
        }
        finally
        {
            _isBusy = false;
            // Only the button's enabled state, never the status text: a field the operator edited during the
            // sign-in round trip must not let this overwrite the outcome that round trip just produced (the
            // Declined/Unreachable/Authorized text set above) with an "unavailable" message.
            _signIn.IsEnabled = _IsSignInAvailable() && !_isBusy;
        }
    }

    private void _OnFieldsChanged()
    {
        var available = _IsSignInAvailable();
        _signIn.IsEnabled = available && !_isBusy;
        if (!available)
        {
            _authStatus.Text = _storedName is null
                ? "Save this connection first — signing in files the token under its name."
                : "Save the connection first — the sign-in is filed under the name and URL it's stored as.";
        }
    }

    /// <summary>
    /// Whether this row still stands for the connection the store knows by <see cref="_storedName"/> and
    /// <see cref="_storedUrl"/> — the same name-drift guard <c>EditableMcpServerViewModel.IsSignInAvailable</c>
    /// uses, extended to the URL: a token is filed under the server's registered name against the authority it was
    /// saved with, so an edited-but-unsaved name <em>or</em> URL must block sign-in, not just the name — otherwise
    /// a sign-in against a changed URL would authorize an issuer the connection is not (yet, or any longer) saved
    /// as pointing at, filed under a name that, once saved, points somewhere else.
    /// </summary>
    private bool _IsSignInAvailable() =>
        _storedName is not null
        && _storedUrl is not null
        && string.Equals($"Depot: {(_name.Text ?? string.Empty).Trim()}", _storedName, StringComparison.Ordinal)
        && string.Equals((_url.Text ?? string.Empty).Trim().TrimEnd('/'), _storedUrl, StringComparison.Ordinal);

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
