using Avalonia.Controls;

namespace Cockpit.Plugin.Browser.UI.Engine;

internal abstract class BrowserView : Control
{
    internal Guid TabId { get; } = Guid.NewGuid();
    internal virtual Guid HostGeneration => Guid.Empty;
    internal virtual bool IsScenePlaceholder => false;

    public event Action<string>? AddressChanged;
    public event Action<string>? TitleChanged;
    public event Action<string>? FaviconChanged;
    public event Action<bool, bool, bool>? LoadingChanged;
    public event Action<bool>? AudibleChanged;
    public event Action? CodecUnsupported;
    public event Action<string>? PopupRequested;
    public event Action? OperatorInput;
    public event Action<string, string>? SubmitHeld;

    protected void OnAddressChanged(string address) => AddressChanged?.Invoke(address);
    protected void OnTitleChanged(string title) => TitleChanged?.Invoke(title);
    protected void OnFaviconChanged(string url) => FaviconChanged?.Invoke(url);
    protected void OnLoadingChanged(bool loading, bool back, bool forward) => LoadingChanged?.Invoke(loading, back, forward);
    protected void OnAudibleChanged(bool audible) => AudibleChanged?.Invoke(audible);
    protected void OnCodecUnsupported() => CodecUnsupported?.Invoke();
    protected void OnPopupRequested(string url) => PopupRequested?.Invoke(url);
    protected void OnOperatorInput() => OperatorInput?.Invoke();
    protected void OnSubmitHeld(string method, string url) => SubmitHeld?.Invoke(method, url);

    public abstract void Navigate(string url);
    public abstract void GoBack();
    public abstract void GoForward();
    public abstract void Reload();
    public abstract bool IsMuted { get; }
    public abstract void SetMuted(bool muted);
    public abstract Task CloseAsync();

    // AC-2149: the explicit host harness cannot grant agent control before host-side authorization exists.
    public virtual string? ControlOrigin { get; set; }
    public virtual void ReleaseHeldSubmit() { }
    public virtual void DropHeldSubmit() { }
    public virtual Task<bool> AgentClick(int x, int y, Func<string?, string?, bool> gate, string frames) => Task.FromResult(false);
    public virtual Task<bool> AgentType(string text, Func<string?, string?, bool> gate) => Task.FromResult(false);
    public virtual Task<bool> AgentKey(int code, Func<string?, string?, bool> gate) => Task.FromResult(false);
    public virtual Task<bool> AgentScroll(int dx, int dy, Func<string?, string?, bool> gate) => Task.FromResult(false);
    public virtual Task<bool> AgentNavigate(string url, Func<string?, string?, bool> gate) => Task.FromResult(false);
    public virtual Task<string?> FrameSignatureAsync() => Task.FromResult<string?>(null);
    public virtual Task<string?> DevToolsAsync(string method, IReadOnlyDictionary<string, object> parameters) => Task.FromResult<string?>(null);
}
