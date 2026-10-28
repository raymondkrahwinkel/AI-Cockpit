// AC-1896 spike (G7): the bridge's dependencies inside a plugin package. Nothing here runs until Run is called, so a host that loads
// the plugin pays for the assembly only; the Jint engine and the bundle come in on the first use (the lazy path the report measures).
using System.Diagnostics;
using Jint;
using YDotNet.Document;

namespace PluginProbe;

public static class Probe
{
    private static readonly Lazy<Engine> Bridge = new(() =>
    {
        var engine = new Engine();
        engine.Execute("globalThis.crypto = { subtle: {}, getRandomValues: (a) => { for (let i = 0; i < a.length; i++) a[i] = Math.floor(Math.random() * 4294967296); return a; } };");
        using var stream = typeof(Probe).Assembly.GetManifestResourceStream("bridge.jint.js")!;
        engine.Execute(new StreamReader(stream).ReadToEnd());
        return engine;
    });

    public static string Run(string markdown, string expectedPm)
    {
        var lines = new List<string>();
        var sw = Stopwatch.StartNew();
        var parsed = Bridge.Value.Invoke(Bridge.Value.GetValue("DepotNotes").AsObject().Get("parse"), markdown).AsString();
        lines.Add($"jint: first use (engine + bundle + parse) {sw.ElapsedMilliseconds} ms, parse == pm.json: {parsed == expectedPm}");
        sw.Restart();
        Bridge.Value.Invoke(Bridge.Value.GetValue("DepotNotes").AsObject().Get("parse"), markdown);
        lines.Add($"jint: second parse {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        using var doc = new Doc();
        var text = doc.Text("t");
        using (var tx = doc.WriteTransaction())
        {
            text.Insert(tx, 0, "yrs loaded");
            tx.Commit();
        }
        byte[] update;
        using (var tx = doc.ReadTransaction())
        {
            update = tx.StateDiffV1(null);
        }
        lines.Add($"ydotnet: native yrs loaded, v1 update {update.Length} B, {sw.ElapsedMilliseconds} ms");
        return string.Join(Environment.NewLine, lines);
    }
}
