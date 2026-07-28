using Avalonia.Controls;
using Cockpit.App.Services;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-367. Every session and pane lives in the one main window, so a modal owned by it stopped the whole
/// cockpit — an agent asking for consent could not be answered until the window in front was closed.
/// <para>
/// What is asserted here is <see cref="Window.IsDialog"/>, and deliberately not "the main window still takes
/// input": modality is applied by the platform, and the headless backend does not apply it — the owner reports
/// <c>IsEnabled == true</c> throughout a real <c>ShowDialog</c>. A test built on input would pass with the fix
/// reverted. <c>IsDialog</c> is the one thing that measures which of the two calls was made.
/// </para>
/// </summary>
[Collection("avalonia")]
public sealed class SurfaceWindowsTests
{
    [Fact]
    public async Task ShowAsync_OpensTheSurfaceBesideTheOwner_NotOverIt()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var surfaces = new SurfaceWindows();
            var owner = _ShownOwner();
            var surface = new Window();

            var pending = surfaces.ShowAsync("key", surface, owner);

            Assert.False(surface.IsDialog);
            Assert.False(pending.IsCompleted);
            Assert.Contains(surface, owner.OwnedWindows);

            surface.Close();
            await pending;

            owner.Close();
        });
    }

    [Fact]
    public async Task ShowAsync_AskedAgainWhileOpen_BringsTheOpenOneForwardInsteadOfMakingASecond()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var surfaces = new SurfaceWindows();
            var owner = _ShownOwner();
            var first = new Window();
            var second = new Window();

            var pending = surfaces.ShowAsync("key", first, owner);
            var again = surfaces.ShowAsync("key", second, owner);

            Assert.Same(pending, again);
            Assert.False(second.IsVisible);
            Assert.Single(owner.OwnedWindows);

            // ⚠️ Not asserted, because it cannot be: that the open window is also brought *forward*. Measured —
            // Activate() is a no-op in this backend, leaving IsActive false and never raising Activated, on the
            // owner as well. So removing the Activate() call from SurfaceWindows escapes every test here, and a
            // second click on Projects would silently do nothing visible. It is checked by hand instead (AC-367,
            // IL#9). Recorded rather than papered over with an assertion that would pass either way.

            first.Close();
            await pending;

            owner.Close();
        });
    }

    [Fact]
    public async Task ShowAsync_OnceTheSurfaceHasClosed_OpensAFreshOneUnderTheSameKey()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var surfaces = new SurfaceWindows();
            var owner = _ShownOwner();

            var first = new Window();
            var pending = surfaces.ShowAsync("key", first, owner);
            first.Close();
            await pending;

            var second = new Window();
            var reopened = surfaces.ShowAsync("key", second, owner);

            Assert.True(second.IsVisible);
            Assert.NotSame(pending, reopened);

            second.Close();
            await reopened;

            owner.Close();
        });
    }

    [Fact]
    public async Task ShowAsync_WithAnAnswer_ReadsWhatTheSurfaceLeftBehind()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var surfaces = new SurfaceWindows();
            var owner = _ShownOwner();
            var surface = new Window();

            // Stands in for the view model a real surface leaves its answer on: Close(result) is not readable
            // off a window that was not shown with ShowDialog, so the answer never travels through Close.
            string? answer = null;
            var pending = surfaces.ShowAsync("key", surface, owner, () => answer);

            // Asserted on this overload too, and not only on the one that answers nothing: a mutation round put
            // ShowDialog back here alone and every test stayed green, which would have made the New-session form
            // and the project editor block the cockpit again without a word.
            Assert.False(surface.IsDialog);

            answer = "chosen";
            surface.Close();

            Assert.Equal("chosen", await pending);

            owner.Close();
        });
    }

    [Fact]
    public async Task ShowAsync_WithAnAnswer_ReadsItTheMomentTheWindowCloses_SoAnythingWrittenLaterIsLost()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var surfaces = new SurfaceWindows();
            var owner = _ShownOwner();
            var surface = new Window();

            // The trap this cost a working build over: Close() raises Closed synchronously, and the answer is
            // read there. A real dialog's code-behind closes the window from its own CloseRequested handler, so
            // anything subscribed after that handler writes its answer too late — the caller reads a cancel and
            // the session never starts. Pinned here so the ordering in SessionDialogService has a reason on
            // record, and DialogModalitySplitTests holds the call sites to it.
            string? answer = null;
            var pending = surfaces.ShowAsync("key", surface, owner, () => answer);

            surface.Closed += (_, _) => answer = "written too late";
            surface.Close();

            Assert.Null(await pending);

            owner.Close();
        });
    }

    [Fact]
    public async Task ShowAsync_ByTheTimeTheCallerResumes_TheKeyIsFreeAgain()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var surfaces = new SurfaceWindows();
            var owner = _ShownOwner();
            var surface = new Window();

            var pending = surfaces.ShowAsync("key", surface, owner);

            // Saving a project closes its window and the caller reopens the list from the line after the await.
            // Releasing the caller before clearing the registry would hand that reopen the window that has just
            // closed, and it would never appear.
            //
            // Continued synchronously on purpose: an ordinary `await` in this harness is posted to the dispatcher
            // and runs well after the completion, so it cannot tell the two orders apart — a mutation round put
            // the removal after the release and every test stayed green. This runs in the completion itself,
            // which is where a real awaiter of a TaskCompletionSource resumes.
            var observed = pending.ContinueWith(
                _ => surfaces.TryActivateAsync("key"),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            surface.Close();

            Assert.Null(await observed);

            owner.Close();
        });
    }

    [Fact]
    public async Task ShowAsync_ClosingTheOwner_TakesTheSurfaceWithIt()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var surfaces = new SurfaceWindows();
            var owner = _ShownOwner();
            var surface = new Window();

            var pending = surfaces.ShowAsync("key", surface, owner);
            owner.Close();

            // Nothing in SurfaceWindows does this — Avalonia closes owned windows with their owner, which is why
            // shutting the cockpit down needs no cleanup here. Pinned so a future change of owner notices.
            Assert.False(surface.IsVisible);
            await pending;
        });
    }

    [Fact]
    public async Task HideAll_TakesTheSurfacesOffTheScreenAndPutsThemBack()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var surfaces = new SurfaceWindows();
            var owner = _ShownOwner();
            var first = new Window();
            var second = new Window();

            var pending = surfaces.ShowAsync("first", first, owner);
            var other = surfaces.ShowAsync("second", second, owner);

            // What the screen lock leans on: it is modal over the main window only, and these are siblings of it.
            var restore = surfaces.HideAll();
            Assert.False(first.IsVisible);
            Assert.False(second.IsVisible);

            restore.Dispose();
            Assert.True(first.IsVisible);
            Assert.True(second.IsVisible);

            first.Close();
            second.Close();
            await pending;
            await other;

            owner.Close();
        });
    }

    [Fact]
    public async Task HideAll_ASurfaceClosedWhileHidden_IsNotShownAgain()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var surfaces = new SurfaceWindows();
            var owner = _ShownOwner();
            var surface = new Window();

            var pending = surfaces.ShowAsync("key", surface, owner);
            var restore = surfaces.HideAll();

            // An agent finishing its task can close a surface while the cockpit is locked. Showing a closed window
            // throws, which would take the unlock down with it — so the restore goes by key and finds nothing.
            surface.Close();
            await pending;

            restore.Dispose();

            Assert.False(surface.IsVisible);

            owner.Close();
        });
    }

    private static Window _ShownOwner()
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();

        return owner;
    }
}
