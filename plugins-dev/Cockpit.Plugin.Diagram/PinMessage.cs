namespace Cockpit.Plugin.Diagram;

// AC-849: the wording of a pin's reference in the coupled session — one shared format for diagram and whiteboard so
// which surface and which object a pin is about always reads the same way, including with several windows open at
// once (Q4 of the ticket).
internal static class PinMessage
{
    public static string Compose(string documentTitle, int index, string? objectLabel, string question) =>
        string.IsNullOrWhiteSpace(objectLabel)
            ? $"📍 pin {index} · \"{documentTitle}\" — {question}"
            : $"📍 pin {index} · \"{documentTitle}\" · {objectLabel} — {question}";
}
