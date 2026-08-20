using Avalonia.Controls;
using Cockpit.Core.Wireframe;
using Cockpit.Plugin.Diagram.Collab;
using Cockpit.Plugin.Diagram.Wireframe.Rendering;

namespace Cockpit.Plugin.Diagram.Wireframe;

// AC-911: the wireframe soortkeuze — a screen, or a small flow, that already stands there. Blank is
// WireframeDocument.Empty itself, first and preselected (criterion 7); the rest use what the format could already
// say before this ticket (AC-901 screens, AC-902 goto, AC-903 palette, AC-915 viewport).
internal static class WireframeTemplates
{
    public static readonly SurfaceTemplate Blank = new("Blank", WireframeDocument.Empty);

    public static readonly SurfaceTemplate Login = new("Login screen", """
        screen "Log in"
          column align:center
            label "Welcome back"
            input "Email address"
            input "Password"
            button "Log in" primary
            label "Forgot your password?"
        """);

    public static readonly SurfaceTemplate Settings = new("Settings screen", """
        screen "Settings"
          row h:1
            column w:1
              nav
                item "General" selected
                item "Account"
                item "Notifications"
            column w:3
              group "Profile"
                input "Display name"
                input "Email address"
              group "Notifications"
                checkbox "Desktop notifications" checked
                toggle "Email digest"
              row align:right
                button "Cancel"
                button "Save" primary
        """);

    public static readonly SurfaceTemplate ListAndDetail = new("List + detail", """
        screen "Inbox"
          row h:1
            column w:1
              list
                item "Message from Alice" goto:"Message"
                item "Message from Bob" goto:"Message"
                item "Message from Carol" goto:"Message"
            column w:3
              label "Select a message to read it"

        screen "Message"
          header "Inbox"
            button "Back" goto:"Inbox"
          label "From: Alice"
          label "Subject: Project update"
          textarea "Message body"
        """);

    public static readonly SurfaceTemplate Dashboard = new("Dashboard", """
        screen "Dashboard"
          header "Acme Analytics"
            avatar "Raymond"
          row h:1
            card "Revenue" w:1
              label "128.400"
              progress "Goal" value:64
            card "Active users" w:1
              label "3.214"
              progress "Goal" value:82
            card "Churn" w:1
              label "1.8%"
              progress "Goal" value:18
          row h:1
            sidebar "Filters" w:1
              checkbox "Last 30 days" checked
              checkbox "Compare previous period"
            main w:3
              label "Chart placeholder"
              image
        """);

    public static readonly SurfaceTemplate Form = new("Form", """
        screen "Contact us"
          column align:center
            label "Get in touch"
            input "Name"
            input "Email address"
            select "Topic"
            textarea "Message"
            row align:right
              button "Cancel"
              button "Send" primary
        """);

    public static readonly SurfaceTemplate LandingPage = new("Landing page", """
        screen "Home"
          header "Acme"
            nav
              item "Product"
              item "Pricing"
              item "About"
            button "Sign up" primary
          column align:center
            label "Build faster, ship sooner"
            label "The toolkit teams reach for first"
            button "Get started" primary
          row h:1
            card "Fast" w:1
              label "Ship in minutes, not weeks"
            card "Reliable" w:1
              label "99.99% uptime"
            card "Secure" w:1
              label "SOC 2 Type II"
          footer "Acme"
            label "All rights reserved"
        """);

    public static readonly IReadOnlyList<SurfaceTemplate> All =
        [Blank, Login, Settings, ListAndDetail, Dashboard, Form, LandingPage];

    // Same shape as _PaletteEntry a few hundred lines over in WireframeWorkspaceBody (AC-903): parse, then render —
    // Overview once a template carries more than one screen, Render otherwise, wrapped at ScreenSize so the layout
    // measures the way it does everywhere else.
    public static Control Preview(SurfaceTemplate template)
    {
        var parsed = WireframeParser.Parse(template.Source);
        var screenSize = WireframeRenderer.ScreenSize;

        return parsed.Screens.Count > 1
            ? WireframeRenderer.Overview(parsed.Screens, screenSize)
            : new Panel { Width = screenSize.Width, Height = screenSize.Height, Children = { WireframeRenderer.Render(parsed.Screens[0]) } };
    }
}
