using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Plugins;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One workflow template as the store shows it (#69): what it does, who published it, which plugins its steps come
/// from, and whether it is already installed.
/// <para>
/// A template is not code, but it is not inert either — a flow can carry a shell command — so the row says what the
/// flow needs before you install it, and an installed flow arrives switched off. Reading it before arming it is the
/// operator's own check, and the app does not pretend to have made it for them.
/// </para>
/// </summary>
public partial class StoreTemplateRowViewModel(WorkflowTemplateStoreEntry entry, string indexUrl, bool isInstalled) : ObservableObject
{
    public WorkflowTemplateStoreEntry Entry { get; } = entry;

    /// <summary>Where the index that offered it lives, so its flow can be fetched relative to that.</summary>
    public string IndexUrl { get; } = indexUrl;

    public string Id => Entry.Id;

    public string Name => Entry.Name;

    public string Description => Entry.Description ?? string.Empty;

    public string Author => string.IsNullOrWhiteSpace(Entry.Author) ? "Unknown" : Entry.Author!;

    public string Version => string.IsNullOrWhiteSpace(Entry.Version) ? string.Empty : $"v{Entry.Version}";

    /// <summary>The plugins whose steps this flow uses, said plainly — a flow built on YouTrack is no use without it.</summary>
    public string Needs => Entry.Requires is { Count: > 0 } requires
        ? $"Needs: {string.Join(", ", requires)}"
        : string.Empty;

    public bool HasNeeds => Entry.Requires is { Count: > 0 };

    [ObservableProperty]
    private bool _isInstalled = isInstalled;

    /// <summary>An installed template is offered again as an update rather than a second copy: same id, newer text.</summary>
    public string ActionLabel => IsInstalled ? "Reinstall" : "Install";

    partial void OnIsInstalledChanged(bool value) => OnPropertyChanged(nameof(ActionLabel));
}
