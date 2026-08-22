using System.Runtime.CompilerServices;
using Cockpit.App.Views;
using Xunit.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// What keeps a discarded <see cref="MarkdownView"/> alive. A virtualising panel binds a container before it is
/// attached and is free to drop it again without ever attaching it, so a view can render — and start its rebuild
/// timer — while it is nowhere in the visual tree.
/// </summary>
[Collection("avalonia")]
public sealed class MarkdownViewLifetimeTests(ITestOutputHelper output)
{
    private const string Reply =
        "## What I found\n\nTwo faults that multiply each other.\n\n" +
        "- `release.yml` builds **only the desktop client**\n- a `Dockerfile` but **no workflow**\n\n" +
        "```csharp\nvar grid = new Grid();\nreturn new Border { Child = grid };\n```\n\n" +
        "| Repo | History |\n|------|---------|\n| one | full |\n| two | squashed |\n\n";

    private static int AliveAfterCollect(List<WeakReference> refs)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        return refs.Count(r => r.IsAlive);
    }

    /// <summary>
    /// The one that matters: a view that rendered but was never attached must not outlive the reference to it.
    /// A running DispatcherTimer is rooted by the dispatcher and its tick closes over the view, so a started timer
    /// pins the view and every control it built. The timer stops itself on the first tick with nothing to do — but
    /// that tick only ever happens if the UI thread gets round to it, and the whole point of this bug is a UI thread
    /// that does not.
    /// </summary>
    [Fact]
    public async Task ViewsThatRenderedWithoutEverBeingAttached_AreCollectable()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var text = string.Concat(Enumerable.Repeat(Reply, 8));

            // Built in its own frame: a local still holding the last view would keep one alive on its own and
            // read as a leak.
            [MethodImpl(MethodImplOptions.NoInlining)]
            static List<WeakReference> Churn(string markdown)
            {
                var made = new List<WeakReference>();
                for (var i = 0; i < 40; i++)
                {
                    var view = new MarkdownView { Markdown = markdown };
                    made.Add(new WeakReference(view));
                }

                return made;
            }

            var refs = Churn(text);
            var immediately = AliveAfterCollect(refs);
            output.WriteLine($"alive right after being dropped: {immediately}/40");

            // Give the dispatcher every chance to run the ticks that would stop those timers — poll for it rather
            // than sleep a fixed span, so a slow run still gets to see the timers stop instead of reporting
            // whatever count a single guessed delay happened to catch.
            var afterTicks = immediately;
            for (var wait = 0; wait < 20 && afterTicks > 0; wait++)
            {
                await Task.Delay(20);
                afterTicks = AliveAfterCollect(refs);
            }

            output.WriteLine($"alive after the dispatcher ran: {afterTicks}/40");

            Assert.Equal(0, immediately);
        });
    }
}
