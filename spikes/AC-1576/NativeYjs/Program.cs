// AC-1576 spike: a .NET Yjs client (YDotNet over yrs) on Depot's /hubs/notes, next to the JS editor on the web.
// Not product code. Driven by lines on stdin so a test script can steer it while it stays connected:
//   type <text>   append <text> to the last paragraph (as a person typing: Edited, then Update)
//   para <text>   add a new paragraph with <text> at the end
//   offline       stop the hub connection, keep editing locally
//   online        reconnect, Join again, send what the room is missing (the same diff sync.ts sends)
//   dump          print the fragment as Yjs' toString and the state vector
//   quit
// Usage: NativeYjs <baseUrl> <org> <project> <path> <passwordFile> [--v2]
// --v2 is the counter-proof: updates go out in Yjs' v2 encoding, which the JS side applies as v1.
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using YDotNet.Document;
using YDotNet.Document.Cells;
using YDotNet.Document.Options;
using YDotNet.Document.StickyIndexes;
using YDotNet.Document.Types.XmlElements;
using YDotNet.Document.Types.XmlTexts;

var (baseUrl, org, project, path, pwFile) = (args[0], args[1], args[2], args[3], args[4]);
var mutateV2 = args.Contains("--v2");
var email = "admin@depot.local";

void Log(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");

// --- sign in with the cookie, as a browser tab would (the hub takes nothing else today) ---
var cookies = new CookieContainer();
var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl) };
var loginPage = await http.GetStringAsync("/login");
var antiforgery = Regex.Match(loginPage, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
var signIn = await http.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["__RequestVerificationToken"] = antiforgery, ["returnUrl"] = "/", ["email"] = email,
    ["password"] = File.ReadAllText(pwFile).Trim(), ["rememberMe"] = "false",
}));
Log($"sign-in: {(int)signIn.StatusCode}");
var notePage = await http.GetStringAsync($"/projects/{org}/{project}/view/{Uri.EscapeDataString(path).Replace("%2F", "/")}?mode=collab");
var userId = Regex.Match(notePage, "data-user=\"([0-9a-f-]{36})\"").Groups[1].Value;
Log($"user: {userId}");

// --- the document: same names as editor.ts ---
var clientId = (ulong)Random.Shared.NextInt64(1, uint.MaxValue);
var doc = new Doc(new DocOptions { Id = clientId });
var fragment = doc.XmlFragment("default");
doc.Text("frontmatter");
doc.Map("meta");

HubConnection? hub = null;
string documentId = "";
var joined = false;
var applyingRemote = false;
var gate = new object();
var personTyped = false;
long awarenessClock = 0;
StickyIndex? cursor = null;

doc.ObserveUpdatesV1(e =>
{
    if (applyingRemote || !joined || hub is null || mutateV2) return;
    _ = SendAsync(e.Update);
});
doc.ObserveUpdatesV2(e =>
{
    if (applyingRemote || !joined || hub is null || !mutateV2) return;
    _ = SendAsync(e.Update);
});

async Task SendAsync(byte[] update)
{
    if (personTyped) { personTyped = false; await hub!.InvokeAsync("Edited", documentId); }
    await hub!.InvokeAsync("Update", documentId, update);
    Log($"sent update {update.Length} B");
}

// yrs allows one transaction at a time: the hub's handlers run on the thread pool next to the command loop, so every
// touch of the doc goes through this gate (Monitor is re-entrant, an observer firing inside a commit is fine).
void ApplyRemote(byte[] update)
{
    lock (gate)
    {
        applyingRemote = true;
        try
        {
            using var tx = doc.WriteTransaction();
            tx.ApplyV1(update);
            tx.Commit();
        }
        finally { applyingRemote = false; }
    }
}

string FragmentString()
{
    lock (gate) return FragmentStringLocked();
}

string FragmentStringLocked()
{
    using var tx = doc.ReadTransaction();
    var sb = new StringBuilder();
    for (uint i = 0; i < fragment.ChildLength(tx); i++)
    {
        var child = fragment.Get(tx, i)!;
        sb.Append(child.Tag == OutputTag.XmlElement ? child.XmlElement!.String(tx) : child.XmlText!.String(tx));
    }
    return sb.ToString();
}

// yrs and Yjs print the same tree with a different tag case and attribute order; this is the shape both sides
// reduce it to before comparing (the JS twin is WebDump in ../Journey/Program.cs).
static string Canon(string xml)
{
    static string Walk(System.Xml.Linq.XNode n) => n switch
    {
        System.Xml.Linq.XText t => t.Value.Replace("&", "&amp;").Replace("<", "&lt;"),
        System.Xml.Linq.XElement e => "<" + e.Name.LocalName.ToLowerInvariant()
            + string.Concat(e.Attributes().OrderBy(a => a.Name.LocalName, StringComparer.Ordinal).Select(a => $" {a.Name.LocalName}=\"{a.Value}\""))
            + ">" + string.Concat(e.Nodes().Select(Walk)) + "</" + e.Name.LocalName.ToLowerInvariant() + ">",
        _ => "",
    };
    return string.Concat(System.Xml.Linq.XElement.Parse("<r>" + xml + "</r>", System.Xml.Linq.LoadOptions.PreserveWhitespace).Nodes().Select(Walk));
}

static string Sha(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

string StateVector()
{
    // As {client:clock} sorted by client, the shape the JS side prints from doc.store.
    lock (gate) return StateVectorLocked();
}

string StateVectorLocked()
{
    using var tx = doc.ReadTransaction();
    var b = tx.StateVectorV1(); int p = 0;
    var n = ReadVarUint(b, ref p);
    var pairs = new SortedDictionary<ulong, ulong>();
    for (ulong i = 0; i < n; i++) { var c = ReadVarUint(b, ref p); pairs[c] = ReadVarUint(b, ref p); }
    return string.Join(",", pairs.Select(kv => $"{kv.Key}:{kv.Value}"));
}

// The last paragraph's XmlText, created when the fragment ends in something else.
XmlText LastParagraphText(YDotNet.Document.Transactions.Transaction tx, bool fresh)
{
    var n = fragment.ChildLength(tx);
    var last = n > 0 ? fragment.Get(tx, n - 1) : null;
    XmlElement paragraph;
    if (!fresh && last?.Tag == OutputTag.XmlElement && last.XmlElement!.Tag(tx) == "paragraph")
        paragraph = last.XmlElement!;
    else
        paragraph = fragment.InsertElement(tx, n, "paragraph");
    var inner = paragraph.ChildLength(tx) > 0 ? paragraph.Get(tx, paragraph.ChildLength(tx) - 1) : null;
    return inner?.Tag == OutputTag.XmlText ? inner.XmlText! : paragraph.InsertText(tx, paragraph.ChildLength(tx));
}

void Type(string text, bool fresh)
{
    lock (gate) TypeLocked(text, fresh);
}

void TypeLocked(string text, bool fresh)
{
    personTyped = true;
    using var tx = doc.WriteTransaction();
    var xmlText = LastParagraphText(tx, fresh);
    var at = xmlText.Length(tx);
    xmlText.Insert(tx, at, text);
    cursor?.Dispose();
    cursor = xmlText.StickyIndex(tx, at + (uint)text.Length, StickyAssociationType.Before);
    tx.Commit();
}

// --- awareness, the y-protocols layout: varuint count, then per client varuint id, varuint clock, varstring JSON ---
static void VarUint(List<byte> o, ulong v) { while (v > 0x7f) { o.Add((byte)(0x80 | (v & 0x7f))); v >>= 7; } o.Add((byte)v); }
static ulong ReadVarUint(byte[] b, ref int p) { ulong v = 0; int s = 0; byte x; do { x = b[p++]; v |= (ulong)(x & 0x7f) << s; s += 7; } while (x >= 0x80); return v; }
static long ReadVarInt(byte[] b, ref int p)
{
    // lib0 varint: first byte carries the sign in bit 6.
    byte x = b[p++]; long v = x & 0x3f; var neg = (x & 0x40) != 0; int s = 6;
    while ((x & 0x80) != 0) { x = b[p++]; v |= (long)(x & 0x7f) << s; s += 7; }
    return neg ? -v : v;
}

// yrs' StickyIndex.encode is Yjs' encodeRelativePosition; y-prosemirror wants relativePositionToJSON.
object? RelativePositionJson(StickyIndex? index)
{
    if (index is null) return null;
    var b = index.Encode(); int p = 0;
    var kind = ReadVarUint(b, ref p);
    object? type = null, item = null; string? tname = null;
    if (kind == 0) { var c = ReadVarUint(b, ref p); var k = ReadVarUint(b, ref p); item = new { client = c, clock = k }; }
    else if (kind == 1) { var len = (int)ReadVarUint(b, ref p); tname = Encoding.UTF8.GetString(b, p, len); p += len; }
    else { var c = ReadVarUint(b, ref p); var k = ReadVarUint(b, ref p); type = new { client = c, clock = k }; }
    var assoc = p < b.Length ? ReadVarInt(b, ref p) : 0;
    return new { type, tname, item, assoc };
}

async Task SayPresenceAsync()
{
    if (!joined || hub is null) return;
    object? anchor;
    lock (gate) anchor = RelativePositionJson(cursor);
    var state = JsonSerializer.Serialize(new
    {
        user = new { id = userId, name = "Cockpit (.NET)", role = "editor", color = "#30bced" },
        activity = "editing",
        cursor = anchor is null ? null : new { anchor, head = anchor },
    });
    var o = new List<byte>();
    VarUint(o, 1); VarUint(o, clientId); VarUint(o, (ulong)++awarenessClock);
    var json = Encoding.UTF8.GetBytes(state); VarUint(o, (ulong)json.Length); o.AddRange(json);
    await hub.InvokeAsync("Presence", documentId, o.ToArray());
}

void LogPresence(string from, byte[] data)
{
    int p = 0; var n = ReadVarUint(data, ref p);
    for (ulong i = 0; i < n; i++)
    {
        var id = ReadVarUint(data, ref p); var clock = ReadVarUint(data, ref p);
        var len = (int)ReadVarUint(data, ref p); var json = Encoding.UTF8.GetString(data, p, len); p += len;
        Log($"presence from {from[..6]}… client {id} clock {clock}: {json}");
    }
}

async Task ConnectAndJoinAsync()
{
    hub = new HubConnectionBuilder()
        .WithUrl(new Uri(new Uri(baseUrl), $"/hubs/notes?expectedUser={userId}"), HttpTransportType.WebSockets, o =>
        {
            o.Cookies = cookies;
            o.Headers["Origin"] = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
        })
        .Build();
    hub.On<string, byte[]>("update", (id, data) =>
    {
        if (id != documentId) return;
        try { ApplyRemote(data); Log($"remote update {data.Length} B → {FragmentString().Length} chars"); }
        catch (Exception e) { Log($"APPLY FAILED {e.GetType().Name}: {e.Message}"); }
    });
    hub.On<string, string, byte[]>("presence", (id, from, data) => { if (id == documentId) LogPresence(from, data); });
    hub.On<string, string>("left", (id, from) => Log($"left: {from[..6]}…"));
    hub.On<string>("refused", code => Log($"REFUSED {code}"));
    await hub.StartAsync();

    var result = await hub.InvokeAsync<JsonElement>("Join", new { org, project, path });
    documentId = result.GetProperty("documentId").GetString()!;
    var rows = result.GetProperty("updates").EnumerateArray().Select(r => r.GetProperty("data").GetBytesFromBase64()).ToList();
    Log($"joined {documentId[..8]}… client {clientId} as {result.GetProperty("role").GetString()}, {rows.Count} rows");

    // The room's state vector, from a scratch doc of its rows, so only what the room lacks goes back (sync.ts' "missing").
    using var scratch = new Doc();
    foreach (var row in rows) { using var t = scratch.WriteTransaction(); t.ApplyV1(row); t.Commit(); }
    byte[] serverVector;
    using (var t = scratch.ReadTransaction()) serverVector = t.StateVectorV1();
    foreach (var row in rows) ApplyRemote(row);

    byte[] missing;
    lock (gate) using (var t = doc.ReadTransaction()) missing = mutateV2 ? t.StateDiffV2(serverVector) : t.StateDiffV1(serverVector);
    joined = true;
    if (missing.Length > 2)
    {
        await hub.InvokeAsync("Edited", documentId);
        await hub.InvokeAsync("Update", documentId, missing);
        Log($"sent missing {missing.Length} B after join");
    }
    await SayPresenceAsync();
}

await ConnectAndJoinAsync();
Log($"fragment: {FragmentString()}");

using var heartbeat = new Timer(_ => { _ = SayPresenceAsync().ContinueWith(t => { }); }, null, 5000, 5000);
while (await Console.In.ReadLineAsync() is { } line)
{
    if (line.Length == 0) continue;
    var (verb, rest) = line.IndexOf(' ') is var i and > 0 ? (line[..i], line[(i + 1)..]) : (line, "");
    Log($"> {line}");
    switch (verb)
    {
        case "type": Type(rest, fresh: false); await SayPresenceAsync(); break;
        case "para": Type(rest, fresh: true); await SayPresenceAsync(); break;
        case "offline": joined = false; await hub!.StopAsync(); await hub.DisposeAsync(); Log("offline"); break;
        case "online": await ConnectAndJoinAsync(); break;
        case "dump":
            var canon = Canon(FragmentString());
            Log($"DUMP sv={StateVector()} canon-sha={Sha(canon)}");
            Log($"DUMP canon={canon}");
            break;
        case "quit": if (hub is not null) await hub.StopAsync(); return;
    }
}
