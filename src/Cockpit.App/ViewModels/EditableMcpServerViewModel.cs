using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Mcp;

namespace Cockpit.App.ViewModels;

/// <summary>
/// A mutable, editable view over an immutable <see cref="McpServerConfig"/> for the MCP-servers dialog
/// (#26). Args are edited as one-per-line text; transport/auth are enum selections that drive which
/// fields the dialog shows. <see cref="ToConfig"/> turns the edits back into a config on save.
/// </summary>
public partial class EditableMcpServerViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private McpTransport _transport;

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

    public IReadOnlyList<McpTransport> Transports { get; } = Enum.GetValues<McpTransport>();

    public IReadOnlyList<McpServerAuth> AuthModes { get; } = Enum.GetValues<McpServerAuth>();

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
    }

    partial void OnAuthChanged(McpServerAuth value)
    {
        OnPropertyChanged(nameof(IsApiKeyAuth));
        OnPropertyChanged(nameof(IsOAuthAuth));
    }

    public EditableMcpServerViewModel(McpServerConfig server)
    {
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
    }

    /// <summary>Rebuilds an immutable config from the current edits, keeping only the fields the chosen transport/auth use.</summary>
    public McpServerConfig ToConfig() => new()
    {
        Name = Name.Trim(),
        Transport = Transport,
        Command = IsStdio && !string.IsNullOrWhiteSpace(Command) ? Command.Trim() : null,
        Args = IsStdio
            ? Args.Split('\n').Select(arg => arg.Trim()).Where(arg => arg.Length > 0).ToList()
            : [],
        Url = IsHttp && !string.IsNullOrWhiteSpace(Url) ? Url.Trim() : null,
        Auth = IsHttp ? Auth : McpServerAuth.None,
        ApiKey = IsApiKeyAuth && !string.IsNullOrWhiteSpace(ApiKey) ? ApiKey.Trim() : null,
        OAuthAuthority = IsOAuthAuth && !string.IsNullOrWhiteSpace(OAuthAuthority) ? OAuthAuthority.Trim() : null,
        OAuthClientId = IsOAuthAuth && !string.IsNullOrWhiteSpace(OAuthClientId) ? OAuthClientId.Trim() : null,
        Enabled = Enabled,
    };
}
