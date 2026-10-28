// AC-1896 spike: G1/G2 without a server, as a probe before the hub run. The JS side is the web's own yjs + y-tiptap in Jint, so what it
// reads is what the web tab would read; the hub run (Hub.cs) is the measurement that counts.
//   A  YDotNet 0.6.0 writes the fragment from *.pm.json (G1) and reads the one the web's code seeds from *.md (G2)
//   B  the workaround: the room itself in Jint (room.js), seeding from *.pm.json and read back by a second Jint room
using System.Text.Json.Nodes;
using Jint;
using YDotNet.Document;
using YDotNet.Document.Cells;
using YDotNet.Document.Options;

internal static class Offline
{
    public static int Run(string bundle, string fixtures)
    {
        var failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            failed += ok ? 0 : 1;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : "  " + detail)}");
        }
        var judge = new JintRoom(bundle, 1);
        foreach (var md in Directory.GetFiles(fixtures, "*.md").Where(f => !f.EndsWith("SOURCE.md")).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(md);
            var text = File.ReadAllText(md);
            var pm = File.ReadAllText(Path.ChangeExtension(md, ".pm.json")).Replace("\r\n", "\n");
            var frontmatter = judge.Eval($"DepotNotes.splitFrontmatter({System.Text.Json.JsonSerializer.Serialize(text)}).frontmatter");
            var eol = text.Contains("\r\n") ? "\r\n" : "\n";
            var normalPm = judge.Call("normalize", pm).AsString();

            // A/G1: YDotNet writes, the web's code reads.
            using (var doc = new Doc(new DocOptions { Id = 2, Encoding = DocEncoding.Utf16 }))
            {
                var fragment = doc.XmlFragment("default");
                var fm = doc.Text("frontmatter");
                var meta = doc.Map("meta");
                string? error = null;
                using (var tx = doc.WriteTransaction())
                {
                    try
                    {
                        YdnFragment.Write(tx, fragment, JsonNode.Parse(pm)!);
                    }
                    catch (Exception e)
                    {
                        error = e.Message;
                    }
                    fm.Insert(tx, 0, frontmatter);
                    using var eolInput = Input.String(eol);
                    meta.Insert(tx, "eol", eolInput);
                    tx.Commit();
                }
                byte[] update;
                using (var tx = doc.ReadTransaction())
                {
                    update = tx.StateDiffV1(null);
                }
                var reader = new JintRoom(bundle, 3);
                reader.Apply(update);
                var read = reader.Call("readPm").AsString();
                var note = reader.Call("readNote").AsString();
                Check($"A G1 {name} web reads pm.json", error is null && reader.Call("normalize", read).AsString() == normalPm, error ?? FirstDiff(normalPm, reader.Call("normalize", read).AsString()));
                Check($"A G1 {name} serializeNote == file", note == text, FirstDiff(text, note));
            }

            // A/G2: the web's code seeds, YDotNet reads.
            var seeder = new JintRoom(bundle, 4);
            seeder.Call("seedMarkdown", text);
            using (var doc = new Doc(new DocOptions { Id = 5, Encoding = DocEncoding.Utf16 }))
            {
                var fragment = doc.XmlFragment("default");
                foreach (var u in seeder.TakeUpdates())
                {
                    using var tx = doc.WriteTransaction();
                    tx.ApplyV1(u);
                    tx.Commit();
                }
                string read;
                using (var tx = doc.ReadTransaction())
                {
                    read = YdnFragment.Read(tx, fragment).ToJsonString();
                }
                string normal;
                try
                {
                    normal = judge.Call("normalize", read).AsString();
                }
                catch (Exception e)
                {
                    normal = "schema refused: " + e.Message;
                }
                Check($"A G2 {name} .NET reads pm.json", normal == normalPm, FirstDiff(normalPm, normal));
            }

            // B: the room in Jint writes from pm.json; another Jint room reads (the same code the web runs, so this is G1 and G2 at once).
            var writer = new JintRoom(bundle, 6);
            writer.Call("seedPm", pm, frontmatter, eol);
            var second = new JintRoom(bundle, 7);
            foreach (var u in writer.TakeUpdates())
            {
                second.Apply(u);
            }
            var back = second.Call("readPm").AsString();
            Check($"B {name} room reads pm.json (raw)", back == pm, FirstDiff(pm, back));
            Check($"B {name} room reads pm.json (schema form)", second.Call("normalize", back).AsString() == normalPm);
            Check($"B {name} serializeNote == file", second.Call("readNote").AsString() == text);
        }
        Console.WriteLine(failed == 0 ? "offline PASS" : $"offline FAIL ({failed})");
        return failed == 0 ? 0 : 1;
    }

    public static string FirstDiff(string expected, string actual)
    {
        var i = 0;
        while (i < expected.Length && i < actual.Length && expected[i] == actual[i])
        {
            i++;
        }
        string Around(string s) => s.Substring(Math.Max(0, i - 30), Math.Min(s.Length - Math.Max(0, i - 30), 70)).Replace("\r", "\\r").Replace("\n", "\\n");
        return i == expected.Length && i == actual.Length ? "" : $"at {i}: expected \"{Around(expected)}\" got \"{Around(actual)}\"";
    }
}
