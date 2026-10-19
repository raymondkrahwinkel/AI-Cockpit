// AC-1576 spike, proof 2: the Depot note editor in Avalonia's NativeWebView.
// A stand-in for the plugin backend signs in over HTTP and hands the webview nothing but the session cookie (HttpOnly),
// so no password and no bearer ever reach the page. The probe then writes through the editor, reads what a browser
// types, and prints the page to PDF as evidence — no screenshot of the operator's screen, no input injection.
// Usage: WebViewProbe <baseUrl> <notePath> <passwordFile> <pdfOut> <seconds>
using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

static class Program
{
    // WebView2 needs an STA UI thread; top-level statements cannot carry the attribute.
    [STAThread]
    static void Main(string[] args) => AppBuilder.Configure<ProbeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

class ProbeApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override async void OnFrameworkInitializationCompleted()
    {
        var a = Environment.GetCommandLineArgs()[1..];
        var (baseUrl, notePath, pwFile, pdfOut, seconds) = (a[0], a[1], a[2], a[3], int.Parse(a[4]));
        void Log(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

        // --- the plugin backend's part: sign in, keep the credential, give the view only a session cookie ---
        var jar = new CookieContainer();
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = jar, AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl) };
        var login = await http.GetStringAsync("/login");
        var token = Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var res = await http.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["returnUrl"] = "/", ["email"] = "admin@depot.local",
            ["password"] = File.ReadAllText(pwFile).Trim(), ["rememberMe"] = "false",
        }));
        var session = jar.GetCookies(new Uri(baseUrl)).Cast<Cookie>().ToList();
        Log($"backend sign-in {(int)res.StatusCode}; cookies for the view: {string.Join(", ", session.Select(c => $"{c.Name}(HttpOnly={c.HttpOnly})"))}");

        var web = new NativeWebView();
        // WebView2's default profile folder sits next to the host exe (dotnet.exe under Program Files): not writable.
        // InPrivate keeps the session cookie in memory only. Reflection, because the docs name the WebView2 args type
        // but not its namespace.
        web.EnvironmentRequested += (_, e) =>
        {
            e.GetType().GetProperty("UserDataFolder")?.SetValue(e, Path.Combine(Path.GetDirectoryName(pdfOut)!, "webview2-profile"));
            e.GetType().GetProperty("IsInPrivateModeEnabled")?.SetValue(e, true);
            Log($"environment: {e.GetType().FullName}");
        };
        var window = new Window { Title = "AC-1576 webview probe", Width = 900, Height = 700, Content = web, ShowActivated = false };
        window.Show();

        var ready = new TaskCompletionSource();
        web.AdapterCreated += (_, _) => ready.TrySetResult();
        await ready.Task;
        var cookies = web.TryGetCookieManager();
        Log($"adapter: {web.AdapterInfo}; cookie manager: {(cookies is null ? "none" : "yes")}");
        foreach (var c in session) cookies!.AddOrUpdateCookie(c);

        var url = new Uri(new Uri(baseUrl), $"/projects/wispslate/spike/view/{Uri.EscapeDataString(notePath).Replace("%2F", "/")}?mode=collab");
        var navigated = new TaskCompletionSource<bool>();
        web.NavigationCompleted += (_, e) => navigated.TrySetResult(e.IsSuccess);
        web.Navigate(url);
        Log($"navigated: {await navigated.Task}");

        async Task<string?> Js(string script) => await Dispatcher.UIThread.InvokeAsync(() => web.InvokeScript(script));
        const string Editor = "document.querySelector('.ProseMirror')?.editor";
        for (var i = 0; i < 60 && await Js($"!!({Editor}) && {Editor}.getText().length > 0") != "true"; i++) await Task.Delay(500);
        Log($"editor live in the webview: {await Js($"!!({Editor})")}, url {await Js("location.pathname")}");
        Log($"document.cookie sees the session: {await Js("document.cookie.includes('" + session.FirstOrDefault()?.Name + "')")}");

        await Js($"{Editor}.commands.insertContentAt({Editor}.state.doc.content.size, '<p>Hallo vanuit de Avalonia-webview</p>')");
        Log("wrote a paragraph through the editor");

        var deadline = DateTime.Now.AddSeconds(seconds);
        string? last = null;
        while (DateTime.Now < deadline)
        {
            var seen = await Js($"{Editor}.getText().includes('van-de-browser')");
            if (seen != last) { Log($"webview sees the browser's text: {seen}"); last = seen; }
            await Task.Delay(500);
        }

        await using (var pdf = await web.PrintToPdfStreamAsync())
        await using (var file = File.Create(pdfOut))
            await pdf.CopyToAsync(file);
        Log($"pdf: {pdfOut}");
        window.Close();
        Environment.Exit(0);
    }
}
