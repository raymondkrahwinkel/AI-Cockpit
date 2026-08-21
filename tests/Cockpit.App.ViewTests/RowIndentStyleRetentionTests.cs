using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.ViewTests;

// AC-998: an icon's template contains a Viewbox, whose internal container is a visual child but no logical child, so
// Avalonia never style-detaches it. A base-type match on the right of a descendant selector styles that container
// too, and its subscription then sits on a live ancestor's Classes forever, holding the whole discarded row.
[Collection("avalonia")]
public sealed class RowIndentStyleRetentionTests
{
    private const int Cycles = 200;

    [Fact]
    public void DiscardedIconsLeaveNoClassListenersOnALiveAncestor()
    {
        HeadlessAvalonia.Run(() =>
        {
            var host = new StackPanel();
            var anchor = new Border { Classes = { "rowRoot", "compact" }, Child = host };
            var window = new Window { Content = anchor, Width = 400, Height = 400 };
            window.Show();
            Pump(window);

            var before = ClassListeners(anchor);

            for (var i = 0; i < Cycles; i++)
            {
                var icon = new MaterialIcon { Kind = MaterialIconKind.Close, Width = 8, Height = 8 };
                host.Children.Add(icon);
                Pump(window);
                host.Children.Remove(icon);
                Pump(window);
            }

            // No GC involved on purpose: the subscription count is exact and monotone, so this cannot flake.
            // Before the fix this counted `before + Cycles`, one per discarded icon.
            Assert.Equal(before, ClassListeners(anchor));
        });
    }

    [Fact]
    public void CompactRowStillFlattensTheIndentOnEveryTypeThatCarriesIt()
    {
        HeadlessAvalonia.Run(() =>
        {
            Control[] Indented() =>
            [
                new Border { Classes = { "rowIndent" } },
                new StackPanel { Classes = { "rowIndent" } },
                new SelectableTextBlock { Classes = { "rowIndent" } },
            ];

            Assert.All(Margins(Indented(), compact: true), m => Assert.Equal(default, m));
            Assert.All(Margins(Indented(), compact: false), m => Assert.Equal(new Thickness(36, 0, 0, 0), m));
        });
    }

    private static IEnumerable<Thickness> Margins(Control[] indented, bool compact)
    {
        var host = new StackPanel();
        foreach (var control in indented)
        {
            host.Children.Add(control);
        }

        var row = new Border { Classes = { "rowRoot" }, Child = host };
        if (compact)
        {
            row.Classes.Add("compact");
        }

        var window = new Window { Content = row, Width = 400, Height = 400 };
        window.Show();
        Pump(window);
        return indented.Select(c => c.Margin).ToList();
    }

    private static void Pump(Window window)
    {
        window.Measure(new Size(400, 400));
        window.Arrange(new Rect(0, 0, 400, 400));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // Classes._listeners -> SafeEnumerableHashSet<IClassesChangedListener>._hashSet. Both are internal to Avalonia;
    // there is no public way to see who is subscribed, and that count is the whole point of this test.
    private static int ClassListeners(StyledElement element)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var classes = element.Classes;
        var listeners = classes.GetType().GetField("_listeners", Flags)?.GetValue(classes)
            ?? throw new InvalidOperationException("Avalonia.Controls.Classes._listeners is gone — update this test.");
        var set = listeners.GetType().GetField("_hashSet", Flags)?.GetValue(listeners)
            ?? throw new InvalidOperationException("SafeEnumerableHashSet._hashSet is gone — update this test.");
        return (int)(set.GetType().GetProperty("Count")?.GetValue(set)
            ?? throw new InvalidOperationException("HashSet.Count is gone — update this test."));
    }
}
