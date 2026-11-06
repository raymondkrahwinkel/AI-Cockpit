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
    public async Task ShowAsync_OpensBesideTheOwner_AndTheKeyIsFreeByTheTimeTheCallerResumes()
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

            // Synchronous on purpose, in the completion where a real awaiter resumes: an ordinary await here is posted
            // to the dispatcher and cannot tell whether the registry was cleared before the caller was released.
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
    public async Task ShowAsync_NeverOwnsOneSurfaceToAnother_EvenAfterAScreenLock()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var surfaces = new SurfaceWindows();
            var main = _ShownOwner();
            var first = new Window();
            var pending = surfaces.ShowAsync("first", first, main);

            // Through a screen lock on the way, because Hide() clears Window.Owner and the restore used to bring
            // the surface back with no owner at all — leaving a parentless surface to be picked as the next owner.
            surfaces.HideAll().Dispose();
            Assert.True(first.IsVisible);
            Assert.Same(main, first.Owner);

            // Callers own a new surface to whichever window is active (PluginDialogHost, the plugin store), which
            // is this one. Avalonia closes owned windows with their owner, so that made two surfaces close as one.
            var second = new Window();
            var alsoPending = surfaces.ShowAsync("second", second, first);

            Assert.Same(main, second.Owner);

            first.Close();
            await pending;

            Assert.True(second.IsVisible);

            second.Close();
            await alsoPending;
            main.Close();
        });
    }

    private static Window _ShownOwner()
    {
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();

        return owner;
    }
}
