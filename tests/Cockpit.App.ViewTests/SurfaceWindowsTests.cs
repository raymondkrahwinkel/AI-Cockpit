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
    public async Task ShowAsync_WithAnAnswer_ReadsItAfterTheWindowClosed()
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

            answer = "chosen";
            surface.Close();

            Assert.Equal("chosen", await pending);

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
            // A TaskCompletionSource resumes its awaiter inline, so releasing the caller before clearing the
            // registry would hand that reopen the window that has just closed — and it would never appear.
            async Task Caller()
            {
                await pending;
                Assert.Null(surfaces.TryActivate("key"));
            }

            var caller = Caller();
            surface.Close();
            await caller;

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

    private static Window _ShownOwner()
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();

        return owner;
    }
}
