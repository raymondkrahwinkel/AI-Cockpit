using Cockpit.Core.Abstractions.Clones;
using Cockpit.Core.Abstractions.Debugging;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.QuickNotes;
using Cockpit.Core.Abstractions.Rendering;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Abstractions.Shortcuts;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.UsagePill;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.HeavyRuns;
using Cockpit.Core.Swarm;
using Cockpit.Core.Terminal;

namespace Cockpit.Core.Abstractions.Remote;

/// <summary>Options stores for a remote window: backend settings and its own client presentation.</summary>
public interface IRemoteOptionsStores : IHeavyRunSettingsStore, ISessionBehaviorSettingsStore, ICloneSettingsStore,
    IWorktreeSettingsStore, INotificationSettingsStore, IDebugSettingsStore, ITerminalSettingsStore,
    ITerminalAccessSettingsStore, IShellAccessSettingsStore
{
    ISwarmOptions Swarm { get; }
    ISwarmLeadership SwarmLeadership { get; }
    Mcp.IMcpServerStore McpServers { get; }
    Mcp.IMcpOAuthCoordinator McpSignIns { get; }
    ILayoutSettingsStore Layout { get; }
    ITranscriptDisplaySettingsStore Transcript { get; }
    IUsagePillSettingsStore UsagePill { get; }
    IVoiceSettingsStore Voice { get; }
    IShortcutSettingsStore Shortcuts { get; }
    IRenderingSettingsStore Rendering { get; }
    IScreenshotSettingsStore Screenshots { get; }
    IQuickNoteSettingsStore QuickNotes { get; }
    IReadOnlyList<ShellDescriptor> Shells { get; }
    bool ReadConfirmedValues { get; set; }
    Task SaveClientPresentationAsync(CancellationToken cancellationToken = default);
}
