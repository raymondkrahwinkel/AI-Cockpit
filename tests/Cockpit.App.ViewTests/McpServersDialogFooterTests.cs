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
/// The MCP-servers dialog keeps Cancel and Save reachable (AC-427). Raymond could not save a server he had just
/// configured, and could not resize the window to get at the buttons either.
/// <para>
/// The mechanism was horizontal, which is why raising the window's height would have fixed nothing: the status
/// line — whose text names every server the cockpit hid — shared an <c>Auto</c> grid column with Cancel and Save,
/// so a long message asked for a footer 947px wide in a 640px window, and a Grid does not clip. The vertical side
/// was sound all along: the footer is docked and the form scrolls under it. Both are asserted here, so neither
/// half can quietly go back.
/// </para>
/// </summary>
[Collection("avalonia")]
public class McpServersDialogFooterTests
{
    // What the view model builds when a server's name collides with one the cockpit already runs
    // (McpServersViewModel), which is the state Raymond was in. The length is the point.
    private const string HiddenServerNotice =
        "Hidden here because the cockpit already runs a server by that name: filesystem, fetch, git. " +
        "Saving removes them — rename yours first if you meant to keep it.";

    private readonly ITestOutputHelper _out;

    public McpServersDialogFooterTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(McpServerAuth.None, "")]
    [InlineData(McpServerAuth.ApiKey, "")]
    [InlineData(McpServerAuth.OAuth, "")]
    // OAuth with a long notice is the tallest configuration and the widest one at once — the case the ticket asks
    // for by name.
    [InlineData(McpServerAuth.OAuth, HiddenServerNotice)]
    public void EveryAuthMode_KeepsTheFooterButtonsInsideTheWindow(McpServerAuth auth, string status)
        => HeadlessAvalonia.Run(() =>
        {
            var window = _Dialog(auth, status, headers: 8);
            var offEdge = _Unreachable(window);
            window.Close();

            Assert.True(offEdge.Count == 0,
                "every footer button has to be inside the dialog, whatever the auth mode says");
        });

    [Fact]
    public void AtItsSmallest_TheButtonsAreStillInside() => HeadlessAvalonia.Run(() =>
    {
        // Sized before it is shown: a headless window keeps the size it opened at, so assigning afterwards would
        // measure the same 640×460 layout against a smaller number and report a failure that is the test's own.
        var window = _Dialog(McpServerAuth.OAuth, HiddenServerNotice, headers: 8, atMinimumSize: true);
        var offEdge = _Unreachable(window);
        window.Close();

        Assert.True(offEdge.Count == 0, "the minimum size is a size the dialog claims to work at");
    });

    [Fact]
    public void AddingHeaders_PushesTheFormsOwnScrollAndNotTheButtons() => HeadlessAvalonia.Run(() =>
    {
        var few = _Dialog(McpServerAuth.OAuth, string.Empty, headers: 1);
        var footerWithFew = _SaveButtonTop(few);
        var extentWithFew = _FormScroller(few).Extent.Height;
        few.Close();

        var many = _Dialog(McpServerAuth.OAuth, string.Empty, headers: 10);
        var scroller = _FormScroller(many);
        var footerWithMany = _SaveButtonTop(many);
        var (extent, viewport) = (scroller.Extent.Height, scroller.Viewport.Height);
        many.Close();

        _out.WriteLine($"footer top: {footerWithFew:0.#} → {footerWithMany:0.#}; " +
                       $"form extent {extentWithFew:0.#} → {extent:0.#} in a {viewport:0.#} viewport");

        Assert.True(extent > extentWithFew, "ten headers have to measure taller than one");
        Assert.True(extent > viewport, "the form has to overflow for scrolling to be what absorbs it");
        Assert.True(Math.Abs(footerWithMany - footerWithFew) <= 0.5,
            $"the footer does not move when the form grows, but went {footerWithFew:0.#} → {footerWithMany:0.#}");
    });

    [Fact]
    public void TheHiddenServerNotice_WrapsRatherThanRunningOffTheEdge() => HeadlessAvalonia.Run(() =>
    {
        var window = _Dialog(McpServerAuth.OAuth, HiddenServerNotice, headers: 2);

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
        Assert.True(height > 20, $"at this length it has to have taken more than one line, but measured {height:0.#}");
    });

    private static McpServersDialog _Dialog(McpServerAuth auth, string status, int headers, bool atMinimumSize = false)
    {
        var viewModel = new McpServersViewModel { StatusMessage = status };
        var server = viewModel.Servers[0];
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

        var window = new McpServersDialog { DataContext = viewModel };
        if (atMinimumSize)
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
        }

        window.Show();
        window.UpdateLayout();
        return window;
    }

    // The footer buttons an operator cannot press: laid out past an edge, or squeezed to nothing — which is what
    // became of Remove when the star column collapsed. Reported as it goes, so a failure names them.
    private List<string> _Unreachable(Window window)
    {
        var unreachable = new List<string>();
        foreach (var button in window.GetVisualDescendants().OfType<Button>()
                     // Matched by content rather than cast: the window's own title bar (AC-335) brings caption
                     // buttons whose content is an icon.
                     .Where(button => button.Content is "Remove" or "Cancel" or "Save"))
        {
            var corner = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window) ?? default;
            if (corner.X <= window.Width + 1 && corner.Y <= window.Height + 1 && button.Bounds.Width >= 1)
            {
                continue;
            }

            unreachable.Add((string)button.Content!);
            _out.WriteLine($"unreachable: {button.Content} ends at x {corner.X:0.#} y {corner.Y:0.#} " +
                           $"width {button.Bounds.Width:0.#}, in a window of {window.Width:0.#}×{window.Height:0.#}");
        }

        return unreachable;
    }

    // The form's scroller, told apart from the list's and from the ones inside every text box by being the tallest.
    private static ScrollViewer _FormScroller(Window window)
        => window.GetVisualDescendants().OfType<ScrollViewer>().OrderByDescending(view => view.Bounds.Height).First();

    private static double _SaveButtonTop(Window window)
    {
        var save = window.GetVisualDescendants().OfType<Button>().First(button => button.Content is "Save");
        return (save.TranslatePoint(new Point(0, 0), window) ?? default).Y;
    }
}
