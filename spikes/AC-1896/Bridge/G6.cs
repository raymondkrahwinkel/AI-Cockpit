// AC-1896 spike, G6: a Markdown offset <-> a Yjs RelativePosition (relativePositionToJSON), both ways, in a paragraph, a heading, a list
// item and a table cell, against a local Depot.
//   .NET -> web  the .NET caret goes out in Presence (y-protocols awareness, `cursor: { anchor, head }`); the web must draw it (span.nt-caret)
//                right before the character the Markdown offset points at
//   web -> .NET  the web puts its caret before a known character; its awareness reaches .NET, which must land on the same Markdown offset
using System.Text;
using System.Text.Json;
using Jint;
using Microsoft.Playwright;

internal static class G6
{
    private static void VarUint(List<byte> o, ulong v)
    {
        while (v > 0x7f)
        {
            o.Add((byte)(0x80 | (v & 0x7f)));
            v >>= 7;
        }
        o.Add((byte)v);
    }

    private static ulong ReadVarUint(byte[] b, ref int p)
    {
        ulong v = 0;
        var s = 0;
        byte x;
        do
        {
            x = b[p++];
            v |= (ulong)(x & 0x7f) << s;
            s += 7;
        }
        while (x >= 0x80);
        return v;
    }

    public static async Task<int> RunAsync(string baseUrl, string secretFile, string bundle, string fixture)
    {
        var failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            failed += ok ? 0 : 1;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length == 0 ? "" : "  " + detail)}");
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("PW_CHROMIUM") });
        await using var context = await browser.NewContextAsync(new() { BypassCSP = true });
        var page = await context.NewPageAsync();
        await Login.WebAsync(page, baseUrl, secretFile);
        var cookies = await Login.HttpAsync(baseUrl, secretFile);
        var path = $"ac1896/{DateTime.Now:HHmmss}-g6.md";
        Console.WriteLine($"     create: {(await Hub.CreateAsync(page, baseUrl, path, File.ReadAllText(fixture)))[..3]}");
        var notePage = $"{baseUrl}/projects/{Hub.Org}/{Hub.Project}/view/{path}?mode=collab";
        await page.GotoAsync(notePage);
        await Hub.WaitWebAsync(page, w => w.GetProperty("fragment").GetInt32() > 0);
        await Task.Delay(1500);

        var clientId = (ulong)Random.Shared.Next(1, int.MaxValue);
        var room = new JintRoom(bundle, (uint)clientId);
        var userId = await Hub.UserIdAsync(baseUrl, cookies, notePage);
        await using var client = new NoteClient(baseUrl, cookies, userId);
        var lastWebCursor = "";
        client.OnPresence = (_, data) =>
        {
            int p = 0;
            var n = ReadVarUint(data, ref p);
            for (ulong i = 0; i < n; i++)
            {
                var id = ReadVarUint(data, ref p);
                ReadVarUint(data, ref p);
                var len = (int)ReadVarUint(data, ref p);
                var json = Encoding.UTF8.GetString(data, p, len);
                p += len;
                if (id != clientId && JsonDocument.Parse(json).RootElement is { ValueKind: JsonValueKind.Object } st && st.TryGetProperty("cursor", out var c) && c.ValueKind == JsonValueKind.Object)
                {
                    lastWebCursor = c.GetProperty("anchor").GetRawText();
                }
            }
        };
        var join = await client.JoinAsync(Hub.Org, Hub.Project, path, "77f8a24281ff", save: false);
        join.Rows.ForEach(room.Apply);
        room.TakeUpdates();
        room.Call("loadMirror");
        var mirror = room.Call("readNote").AsString();
        ulong clock = 0;

        // Markdown text before which the caret goes: its first character is the one the caret must sit in front of.
        var targets = new (string Kind, string Before)[]
        {
            ("paragraph", "and italic"),
            ("heading", "ructure"),
            ("list item", "ck to two"),
            ("table cell", "ight"),
        };
        foreach (var (kind, before) in targets)
        {
            var offset = mirror.IndexOf(before, StringComparison.Ordinal);
            if (offset < 0)
            {
                Check($"G6 {kind}: target in the fixture", false, $"\"{before}\" not in the note");
                continue;
            }
            // .NET -> web
            var relative = room.Call("relativeAtOffset", offset).AsString();
            var state = $"{{\"user\":{{\"id\":\"{userId}\",\"name\":\"Cockpit (.NET)\"}},\"activity\":\"editing\",\"cursor\":{{\"anchor\":{relative},\"head\":{relative}}}}}";
            var o = new List<byte>();
            VarUint(o, 1);
            VarUint(o, clientId);
            VarUint(o, ++clock);
            var bytes = Encoding.UTF8.GetBytes(state);
            VarUint(o, (ulong)bytes.Length);
            o.AddRange(bytes);
            await client.PresenceAsync(o.ToArray());
            string drawn = "";
            for (var i = 0; i < 30 && drawn.Length == 0; i++)
            {
                await Task.Delay(100);
                drawn = await page.EvaluateAsync<string>("""
                    () => { const c = document.querySelector('.ProseMirror .nt-caret'); if (!c) return '';
                            const r = document.createRange(); r.setStartAfter(c); r.setEnd(document.querySelector('.ProseMirror'), document.querySelector('.ProseMirror').childNodes.length);
                            return r.toString().slice(0, 6); }
                    """);
            }
            Check($"G6 {kind}: web draws the .NET caret before \"{before[..3]}\"", drawn.StartsWith(before[..Math.Min(6, before.Length)], StringComparison.Ordinal) || before.StartsWith(drawn, StringComparison.Ordinal) && drawn.Length > 0,
                $"offset {offset}, after the drawn caret: \"{drawn}\", relative {relative}");

            // web -> .NET
            lastWebCursor = "";
            await page.EvaluateAsync("""
                (t) => { const ed = document.querySelector('.ProseMirror').editor; let at = -1;
                         ed.state.doc.descendants((n, pos) => { if (at < 0 && n.isText && n.text.includes(t)) at = pos + n.text.indexOf(t); });
                         ed.commands.focus(); ed.commands.setTextSelection(at); }
                """, before);
            for (var i = 0; i < 80 && lastWebCursor.Length == 0; i++)
            {
                await Task.Delay(100);
            }
            var arrived = lastWebCursor.Length == 0 ? -2 : (int)room.Call("offsetOfRelative", lastWebCursor).AsNumber();
            Check($"G6 {kind}: web caret lands on Markdown offset {offset}", arrived == offset,
                $"got {arrived}{(arrived >= 0 ? $" (\"{mirror.Substring(arrived, Math.Min(6, mirror.Length - arrived))}\")" : arrived == -2 ? " (no web cursor in 8 s)" : "")}");
            await page.EvaluateAsync("() => document.querySelector('.ProseMirror').blur()");
        }
        Console.WriteLine(failed == 0 ? "g6 PASS" : $"g6 FAIL ({failed})");
        return failed == 0 ? 0 : 1;
    }
}
