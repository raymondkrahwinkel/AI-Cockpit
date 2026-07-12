using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.PromptLibrary;

/// <summary>
/// Loads and saves the prompt-template list in the plugin's per-plugin storage (#2), seeding a handful of
/// starter templates the first time the plugin runs so the library is never empty. Templates live under a
/// single storage key as a JSON list; the dialog is the only writer.
/// </summary>
internal sealed class PromptLibrarySettings(IPluginStorage storage)
{
    private const string TemplatesKey = "templates";

    public IReadOnlyList<PromptTemplate> Load()
    {
        var saved = storage.Get<List<PromptTemplate>>(TemplatesKey);
        if (saved is { Count: > 0 })
        {
            return saved;
        }

        var seeded = DefaultTemplates();
        Save(seeded);
        return seeded;
    }

    public void Save(IReadOnlyList<PromptTemplate> templates) =>
        storage.Set(TemplatesKey, new List<PromptTemplate>(templates));

    public static string NewId() => Guid.NewGuid().ToString("N");

    // English by default (the cockpit's UI/prompt language). A couple carry {{variable}} placeholders to show
    // the feature off; the rest are ready to insert as-is.
    private static List<PromptTemplate> DefaultTemplates() =>
    [
        new(NewId(), "Review my changes",
            "Review my current changes for correctness bugs and obvious simplifications. List the most important issues first, and say if you find nothing."),
        new(NewId(), "Explain this",
            "Explain how {{thing}} works, step by step, and note any non-obvious gotchas."),
        new(NewId(), "Write a commit message",
            "Write a concise commit message for my staged changes — one bullet per changed concern, imperative mood, no fluff."),
        new(NewId(), "Refactor",
            "Refactor {{target}} for readability without changing its behaviour. Explain each change briefly."),
        new(NewId(), "Write tests",
            "Write unit tests for {{target}}, covering the main paths and the edge cases that are easy to get wrong."),
    ];
}
