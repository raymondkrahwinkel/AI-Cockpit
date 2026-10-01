using System.Diagnostics.CodeAnalysis;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Projects;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Infrastructure.Hosting;

// AC-1381: what a cockpit with no frontend answers where the desktop has a window to show something on. The bootstrap
// registers these ahead of the frontend's own, and the desktop's registrations win, as the last one does.

// No UI thread to measure: a calibration without one reads no stutter.
internal sealed class NoUiHitchProbe : IUiHitchProbe
{
    public IUiHitchSession Start() => new Session();

    private sealed class Session : IUiHitchSession
    {
        public double MaxHitchMs => 0;

        public void Dispose()
        {
        }
    }
}

// No screens to report: the Linux capture turns an empty list into a refusal that names what is missing.
internal sealed class NoDesktopDisplays : IDesktopDisplays
{
    public Task<IReadOnlyList<DesktopDisplay>> EnumerateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DesktopDisplay>>([]);
}

// No browser of the operator's to open a link in, so every open is the refusal the gateway already words.
internal sealed class NoBrowserLinkOpener : IExternalLinkOpener
{
    public bool TryParseWebAddress(string? url, [NotNullWhen(true)] out Uri? address)
    {
        address = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

        return address is not null;
    }

    public bool TryOpen(Uri address) => false;
}

// The clear is the host's own gate, as on the desktop; a question card needs a pane to be drawn on, and there is none.
internal sealed class HostAssistantConversation(IAssistantSessionHost host) : IAssistantConversation
{
    public bool RequestConversationClear() => host.RequestConversationClear();

    public Task<bool> ShowQuestionAsync(string question, string inputJson) => Task.FromResult(false);
}

// The project dialogs compose a project, and there are none here (AC-1435), so composing refuses with the reason.
internal sealed class NoProjectComposer : IProjectComposer
{
    internal const string Refusal = "This cockpit runs without its project dialogs, and composing a project goes through them.";

    public Task<(Project? Project, string? Refusal)> ComposeSharedProjectAsync(
        string sharedProjectId,
        ISharedProjectSource source,
        string sourceDirectory,
        string profileLabel,
        IReadOnlyList<string>? resourceReferences,
        CancellationToken cancellationToken) =>
        Task.FromResult<(Project?, string?)>((null, Refusal));

    public Task<(Project? Project, string? Refusal)> ComposeNewProjectAsync(
        string name,
        string? description,
        string? sourceDirectory,
        string? behaviorPrompt,
        bool isolateInWorktreeByDefault,
        string? category,
        string? defaultProfileLabel,
        CancellationToken cancellationToken) =>
        Task.FromResult<(Project?, string?)>((null, Refusal));
}
