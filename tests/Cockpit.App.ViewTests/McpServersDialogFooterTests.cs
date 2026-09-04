using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Mcp;
using Xunit.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The MCP-servers footer used to belong to its own 640×460 window (AC-427: Cancel and Save could end up past the
/// edge, sharing an Auto grid column with a status line whose text names every server the cockpit hid). AC-1002
/// moved MCP Servers into Options as a category, replacing that window — Remove and the status line now live in
/// the category's own 180px-wide server-list column, and Cancel/Apply and Close are the dialog's shared footer.
/// These four cases are the same ones AC-427 named, re-pointed at the new home rather than dropped: the shared
/// footer must not lose its buttons to a long validation message, at Options' own minimum working size, or to a
/// server accumulating custom headers; and the hidden-server notice must still wrap instead of running off an edge.
/// </summary>
[Collection("avalonia")]
public class McpServersDialogFooterTests
{
    // What McpServersViewModel.LoadAsync builds when a server's name collides with one the cockpit already runs.
    // The length is the point.
    private const string HiddenServerNotice =
        "Hidden here because the cockpit already runs a server by that name: filesystem, fetch, git. " +
        "Saving removes them — rename yours first if you meant to keep it.";

    // Below Options' own MinHeight is 0 (it declares none), so this is squeezed the same way
    // DialogFooterReachabilityTests squeezes every other dialog: to the smaller of a fixed floor and the dialog's
    // own minimum.
    private const double SqueezedHeight = 200;

    private readonly ITestOutputHelper _out;

    public McpServersDialogFooterTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(McpServerAuth.None, "")]
    [InlineData(McpServerAuth.ApiKey, "")]
    [InlineData(McpServerAuth.OAuth, "")]
    // OAuth with a long notice is the tallest configuration and the one most likely to push the shared footer
    // wide — the case AC-427 named by number.
    [InlineData(McpServerAuth.OAuth, HiddenServerNotice)]
    public void EveryAuthMode_KeepsTheSharedFooterButtonsInsideTheWindow(McpServerAuth auth, string status)
        => HeadlessAvalonia.Run(() =>
        {
            var window = _Options(auth, status, headers: 8);
            var offEdge = _UnreachableFooterButtons(window);
            window.Close();

            Assert.True(offEdge.Count == 0,
                "the shared footer's Cancel and Apply and Close have to stay inside Options, whatever the auth mode says");
        });

    [Fact]
    public void AtItsSmallest_TheSharedFooterButtonsAreStillInside() => HeadlessAvalonia.Run(() =>
    {
        // Sized before it is shown: a headless window keeps the size it opened at, so squeezing afterwards would
        // measure the same layout against a smaller number and report a failure that is the test's own.
        var window = _Options(McpServerAuth.OAuth, HiddenServerNotice, headers: 8, squeezed: true);
        var offEdge = _UnreachableFooterButtons(window);
        window.Close();

        Assert.True(offEdge.Count == 0, "the smallest size Options claims to work at is a size the footer has to work at too");
    });

    [Fact]
    public void AddingHeaders_PushesTheCategorysOwnScrollAndNotTheSharedFooter() => HeadlessAvalonia.Run(() =>
    {
        var few = _Options(McpServerAuth.OAuth, string.Empty, headers: 1);
        var footerWithFew = _ApplyButtonTop(few);
        var extentWithFew = _McpServersScroller(few).Extent.Height;
        few.Close();

        var many = _Options(McpServerAuth.OAuth, string.Empty, headers: 10);
        var scroller = _McpServersScroller(many);
        var footerWithMany = _ApplyButtonTop(many);
        var (extent, viewport) = (scroller.Extent.Height, scroller.Viewport.Height);
        many.Close();

        _out.WriteLine($"footer top: {footerWithFew:0.#} → {footerWithMany:0.#}; " +
                       $"category extent {extentWithFew:0.#} → {extent:0.#} in a {viewport:0.#} viewport");

        Assert.True(extent > extentWithFew, "ten headers have to measure taller than one");
        Assert.True(extent > viewport, "the category page has to overflow for scrolling to be what absorbs it");
        Assert.True(Math.Abs(footerWithMany - footerWithFew) <= 0.5,
            $"the shared footer does not move when the MCP Servers page grows, but went {footerWithFew:0.#} → {footerWithMany:0.#}");
    });

    [Fact]
    public void TheHiddenServerNotice_WrapsRatherThanRunningOffTheEdge() => HeadlessAvalonia.Run(() =>
    {
        var window = _Options(McpServerAuth.OAuth, HiddenServerNotice, headers: 2);

        var notice = window.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Text == HiddenServerNotice);
        var right = (notice.TranslatePoint(new Point(notice.Bounds.Width, 0), window) ?? default).X;
        var height = notice.Bounds.Height;
        var wrapping = notice.TextWrapping;

        _out.WriteLine($"notice: width={notice.Bounds.Width:0.#} height={height:0.#} right={right:0.#} " +
                       $"wrapping={wrapping}");
        window.Close();

        Assert.True(right <= window.Width + 1,
            $"the notice must not run past the window it is in, but ends at {right:0.#} of {window.Width:0.#}");
        Assert.True(wrapping == TextWrapping.Wrap, "it is a sentence, not a label — it wraps");
        Assert.True(height > 20, $"at this length and this column width it has to have taken more than one line, but measured {height:0.#}");
    });

    // A CockpitViewModel with McpServers populated, on the mcp-servers category, sized (and optionally squeezed)
    // the way _Sized/_Dialog built the old standalone window.
    private static OptionsDialog _Options(McpServerAuth auth, string status, int headers, bool squeezed = false)
    {
        var mcpServers = new McpServersViewModel { StatusMessage = status };
        var server = mcpServers.Servers[0];
        if (auth != McpServerAuth.None)
        {
            server.Transport = McpTransport.Http;
            server.Url = "https://mcp.example.com/mcp";
            server.Auth = auth;
            server.OAuthAuthority = "https://login.example.com";
        }

        for (var index = 0; index < headers; index++)
        {
            server.AddHeaderCommand.Execute(null);
        }

        var window = new OptionsDialog { DataContext = new CockpitViewModel { McpServers = mcpServers } };

        if (squeezed)
        {
            window.MaxHeight = Math.Max(window.MinHeight, SqueezedHeight);
            window.Height = window.MaxHeight;
        }

        window.Show();
        window.SelectCategory("mcp-servers");
        window.UpdateLayout();
        return window;
    }

    // The footer buttons an operator cannot press: laid out past an edge, or squeezed to nothing. Matched by
    // content rather than cast, same as the window this replaces — Options' own chrome brings caption buttons
    // whose content is an icon. Restricted to the shared footer's own pair: several categories (Security's node
    // pairing, Voice's calibration) have their own "Cancel" button too, sitting invisible inside their own
    // ScrollViewer while a different category is selected — exactly the check `IsEffectivelyVisible` skips.
    private List<string> _UnreachableFooterButtons(Window window)
    {
        var unreachable = new List<string>();
        foreach (var button in window.GetVisualDescendants().OfType<Button>()
                     .Where(button => button.Content is "Cancel" or "Apply and Close" && button.IsEffectivelyVisible))
        {
            var corner = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window) ?? default;
            if (corner.X <= window.Bounds.Width + 1 && corner.Y <= window.Bounds.Height + 1 && button.Bounds.Width >= 1)
            {
                continue;
            }

            unreachable.Add((string)button.Content!);
            _out.WriteLine($"unreachable: {button.Content} ends at x {corner.X:0.#} y {corner.Y:0.#} " +
                           $"width {button.Bounds.Width:0.#}, in a window of {window.Bounds.Width:0.#}×{window.Bounds.Height:0.#}");
        }

        return unreachable;
    }

    // The MCP Servers category's own ScrollViewer, told apart from every other category's by its Tag.
    private static ScrollViewer _McpServersScroller(Window window)
        => window.GetVisualDescendants().OfType<ScrollViewer>().First(view => (string?)view.Tag == "mcp-servers");

    private static double _ApplyButtonTop(Window window)
    {
        var apply = window.GetVisualDescendants().OfType<Button>().First(button => button.Content is "Apply and Close");
        return (apply.TranslatePoint(new Point(0, 0), window) ?? default).Y;
    }
}
