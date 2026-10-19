// AC-1576 spike journey: points 1-3 of the ticket, repeatable against a local Depot of epic/DEP-385.
//   1. web and .NET type into the same note at the same time; afterwards both hold the same tree and state vector
//   2. the .NET client's cursor is drawn on the web, and the web's cursor reaches the .NET client
//   3. the .NET client is offline for 30 s and keeps typing, the web too; after it reconnects nothing is lost
// Usage: Journey <baseUrl> <passwordFile> <nativeDll> [--v2]   (--v2 passes the counter-proof to the .NET client)
// Exit code 0 only when every check passed.
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

var (baseUrl, pwFile, nativeDll) = (args[0], args[1], args[2]);
var v2 = args.Contains("--v2");
var work = Path.Combine(Path.GetTempPath(), "ac1576-" + DateTime.Now.ToString("HHmmss"));
Directory.CreateDirectory(work);
var nativeLog = Path.Combine(work, "native.log");
var failures = 0;

// Where two trees part, and what the web has from there on.
static string FirstDiff(string a, string b)
{
    var i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
    return $"at {i} (web {a.Length}, .NET {b.Length}): web-tail={System.Text.Json.JsonSerializer.Serialize(a[Math.Min(i, a.Length)..])}";
}

void Check(string name, bool ok, string detail = "")
{
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
}

string NativeLog() { using var s = new FileStream(nativeLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return new StreamReader(s).ReadToEnd(); }
Process? native = null;
async Task Native(string command) { await native!.StandardInput.WriteLineAsync(command); await native.StandardInput.FlushAsync(); await Task.Delay(400); }
async Task<Match> NativeDump()
{
    var before = Regex.Matches(NativeLog(), "DUMP sv=").Count;
    await Native("dump");
    for (var i = 0; i < 40 && Regex.Matches(NativeLog(), "DUMP sv=").Count == before; i++) await Task.Delay(250);
    return Regex.Matches(NativeLog(), @"DUMP sv=(\S*) canon-sha=(\S+)\r?\n[^\r\n]*DUMP canon=([^\r\n]*)").Last();
}

// The JS twin of NativeYjs' Canon and StateVector: lowercase tags, attributes in ordinal order, {client:clock} sorted.
const string WebDump = """
() => {
  const ed = document.querySelector('.ProseMirror').editor;
  const doc = ed.extensionManager.extensions.find(e => e.name === 'collaboration').options.document;
  const xml = new DOMParser().parseFromString('<r>' + doc.getXmlFragment('default').toString() + '</r>', 'application/xml');
  const esc = s => s.replaceAll('&', '&amp;').replaceAll('<', '&lt;');
  const walk = n => n.nodeType === 3 ? esc(n.nodeValue) : n.nodeType !== 1 ? '' :
    '<' + n.nodeName.toLowerCase() + [...n.attributes].sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0).map(a => ` ${a.name}="${a.value}"`).join('') + '>'
    + [...n.childNodes].map(walk).join('') + '</' + n.nodeName.toLowerCase() + '>';
  const canon = [...xml.documentElement.childNodes].map(walk).join('');
  const sv = [...doc.store.clients.entries()].map(([c, structs]) => { const l = structs[structs.length - 1]; return [c, l.id.clock + l.length]; })
    .sort((a, b) => a[0] - b[0]).map(([c, k]) => c + ':' + k).join(',');
  return { canon, sv, clientID: doc.clientID };
}
""";

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("PW_CHROMIUM") });
var page = await (await browser.NewContextAsync()).NewPageAsync();

// Sign in on the web; the password is read from the file and never printed.
await page.GotoAsync($"{baseUrl}/login");
await page.FillAsync("input[name=email]", "admin@depot.local");
await page.FillAsync("input[name=password]", File.ReadAllText(pwFile).Trim());
await page.PressAsync("input[name=password]", "Enter");
await page.WaitForURLAsync(u => !u.Contains("/login"));

// A fresh note per run, through the same dialog a person uses.
var subject = "Journey " + DateTime.Now.ToString("HHmmss");
await page.GotoAsync($"{baseUrl}/projects/wispslate/spike");
await page.GetByRole(AriaRole.Button, new() { Name = "✎ Meetingnotitie" }).ClickAsync();
await page.Locator("dialog[open] input:visible:not([type=radio])").First.FillAsync(subject);
await page.GetByRole(AriaRole.Button, new() { Name = "Aanmaken en openen" }).ClickAsync();
await page.WaitForURLAsync(u => u.Contains("mode=collab"));
await page.Locator(".ProseMirror").WaitForAsync();
await page.WaitForFunctionAsync("() => document.querySelector('.ProseMirror')?.editor && document.querySelector('.ProseMirror').innerText.includes('Actiepunten')");
var notePath = Uri.UnescapeDataString(Regex.Match(page.Url, "/view/([^?]+)").Groups[1].Value);
Console.WriteLine($"note: {notePath}  work: {work}");

// The .NET client on the same note.
native = Process.Start(new ProcessStartInfo("dotnet", new[] { nativeDll, baseUrl, "wispslate", "spike", notePath, pwFile }.Concat(v2 ? ["--v2"] : []))
{
    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
})!;
var logWriter = new StreamWriter(new FileStream(nativeLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
native.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (logWriter) logWriter.WriteLine(e.Data); };
native.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (logWriter) logWriter.WriteLine("ERR " + e.Data); };
native.BeginOutputReadLine(); native.BeginErrorReadLine();
for (var i = 0; i < 120 && !NativeLog().Contains("fragment:"); i++) await Task.Delay(250);
var nativeClient = Regex.Match(NativeLog(), @"client (\d+) as").Groups[1].Value;
Check("native client joined the room", nativeClient.Length > 0, nativeClient);

try
{
    // --- 1. at the same time ---
    var nativeWords = Enumerable.Range(1, 12).Select(i => $"net{i}").ToArray();
    var webTyping = Task.Run(async () =>
    {
        await page.Locator(".ProseMirror ul:not([data-type=taskList]) li p").First.ClickAsync();
        await page.Keyboard.TypeAsync("web-gelijktijdig-0123456789", new() { Delay = 60 });
    });
    await Native("para net0 ");
    foreach (var w in nativeWords) await Native("type " + w + " ");
    await webTyping;
    await Task.Delay(2500);

    var web = await page.EvaluateAsync<WebState>(WebDump);
    var net = await NativeDump();
    Check("1 web holds the .NET text", nativeWords.All(w => web.Canon.Contains(w + " ")));
    Check("1 .NET holds the web text", net.Groups[3].Value.Contains("web-gelijktijdig-0123456789"));
    Check("1 same tree after merging", web.Canon == net.Groups[3].Value, web.Canon == net.Groups[3].Value ? $"{web.Canon.Length} chars" : FirstDiff(web.Canon, net.Groups[3].Value));
    Check("1 same state vector", web.Sv == net.Groups[1].Value, $"web {web.Sv} | .NET {net.Groups[1].Value}");

    // --- 2. presence both ways ---
    await Task.Delay(1000);
    var caret = await page.EvaluateAsync<string?>($$"""
        () => {
          const ed = document.querySelector('.ProseMirror').editor;
          const aw = ed.extensionManager.extensions.find(e => e.name === 'collaborationCaret').options.provider.awareness;
          const s = aw.getStates().get({{nativeClient}});
          if (!s?.cursor) return null;
          const carets = [...document.querySelectorAll('.nt-caret')].map(c => c.parentElement?.innerText ?? '');
          return carets.find(t => t.includes('net12')) ?? 'state without a drawn caret';
        }
        """);
    Check("2 .NET cursor drawn on the web, in the .NET paragraph", caret is not null && caret.Contains("net12"), caret ?? "no awareness state for the .NET client");
    var webCursorAtNative = Regex.IsMatch(NativeLog(), $@"presence from .*client {web.ClientID} clock \d+: .*""cursor"":\{{""anchor"":\{{");
    Check("2 web cursor reached the .NET client", webCursorAtNative);

    // --- 3. offline for 30 s, both keep typing ---
    await Native("offline");
    await Native("para offline-van-dotnet");
    await Native("type -nog-meer");
    await page.Locator(".ProseMirror ul:not([data-type=taskList]) li p").First.ClickAsync();
    await page.Keyboard.PressAsync("End");
    await page.Keyboard.TypeAsync(" web-tijdens-offline", new() { Delay = 30 });
    Console.WriteLine("… 30 s offline");
    await Task.Delay(30_000);
    await Native("online");
    for (var i = 0; i < 40 && Regex.Matches(NativeLog(), "joined ").Count < 2; i++) await Task.Delay(250);
    await Task.Delay(2500);

    web = await page.EvaluateAsync<WebState>(WebDump);
    net = await NativeDump();
    Check("3 web got the offline .NET text", web.Canon.Contains("offline-van-dotnet-nog-meer"));
    Check("3 .NET got the web text typed while it was away", net.Groups[3].Value.Contains("web-tijdens-offline"));
    Check("3 same tree after reconnect", web.Canon == net.Groups[3].Value, web.Canon == net.Groups[3].Value ? $"{web.Canon.Length} chars" : FirstDiff(web.Canon, net.Groups[3].Value));
    Check("3 same state vector after reconnect", web.Sv == net.Groups[1].Value, $"web {web.Sv} | .NET {net.Groups[1].Value}");
    Check("3 the earlier text survived", web.Canon.Contains("net12") && web.Canon.Contains("web-gelijktijdig-0123456789"));

    // A second web tab, opened fresh, reads the room the way any newcomer does.
    var fresh = await (await browser.NewContextAsync()).NewPageAsync();
    foreach (var c in await page.Context.CookiesAsync()) await fresh.Context.AddCookiesAsync([new Cookie { Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path }]);
    await fresh.GotoAsync(page.Url);
    await fresh.WaitForFunctionAsync("() => document.querySelector('.ProseMirror')?.editor && document.querySelector('.ProseMirror').innerText.includes('Actiepunten')");
    await Task.Delay(1500);
    var newcomer = await fresh.EvaluateAsync<WebState>(WebDump);
    Check("3 a newcomer sees the same tree", newcomer.Canon == web.Canon);
}
finally
{
    await native.StandardInput.WriteLineAsync("quit");
    if (!native.WaitForExit(5000)) native.Kill();
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

class WebState { public string Canon { get; set; } = ""; public string Sv { get; set; } = ""; public long ClientID { get; set; } }
