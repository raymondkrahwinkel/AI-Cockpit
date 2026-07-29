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
/// Sign-in is only offered once the row's current name matches what is actually registered under
/// (<see cref="_storedName"/>) — the same rule <c>EditableMcpServerViewModel.IsSignInAvailable</c> enforces for the
/// host's own MCP-servers dialog, for the same reason: a token is filed under a server's registered name, and
/// signing in under a name that has not been saved yet would file it under a name the store does not have.
/// </para>
/// </summary>
internal sealed class DepotConnectionRowControl : UserControl
{
    private readonly ICockpitHost _host;
    private readonly string _id;
    private readonly string? _storedName;
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

        _name = new TextBox { Text = existing?.Name ?? string.Empty, PlaceholderText = "Name (e.g. Work, Personal)" };
        _url = new TextBox { Text = existing?.Url ?? string.Empty, PlaceholderText = "https://depot.example.com" };
        _authStatus = new TextBlock { FontSize = 11, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };

        _name.TextChanged += (_, _) => _RefreshSignInAvailability();

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

        _RefreshSignInAvailability();
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
            _RefreshSignInAvailability();
        }
    }

    private void _RefreshSignInAvailability()
    {
        var available = _IsSignInAvailable();
        _signIn.IsEnabled = available && !_isBusy;
        if (!available)
        {
            _authStatus.Text = _storedName is null
                ? "Save this connection first — signing in files the token under its name."
                : "Save the new name first — the sign-in is filed under the name this connection is stored as.";
        }
    }

    /// <summary>
    /// Whether this row still stands for the connection the store knows by <see cref="_storedName"/> — the same
    /// name-drift guard <c>EditableMcpServerViewModel.IsSignInAvailable</c> uses, so a sign-in never files a token
    /// under a name that turns out, once saved, to belong to a different (renamed) contribution.
    /// </summary>
    private bool _IsSignInAvailable() =>
        _storedName is not null && string.Equals($"Depot: {(_name.Text ?? string.Empty).Trim()}", _storedName, StringComparison.Ordinal);

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}
