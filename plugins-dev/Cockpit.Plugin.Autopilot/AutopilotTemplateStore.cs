using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Autopilot;

// The persisted half of the Autopilot template system (AC-189): the operator's own templates and their edits of
// the plugin/builtin ones. Same shape as `AutopilotRunQueue`/`AutopilotRunHistory`. Plugin registrations live
// only in memory; `List` merges them (with any override applied) with the operator's own templates.
internal sealed class AutopilotTemplateStore
{
    private const string UserTemplatesKey = "templates";
    private const string OverridesKey = "templateOverrides";
    private readonly IPluginStorage _storage;
    private readonly List<AutopilotTemplate> _userTemplates;
    private readonly List<AutopilotTemplateOverride> _overrides;

    public AutopilotTemplateStore(IPluginStorage storage)
    {
        _storage = storage;
        _userTemplates = storage.Get<List<AutopilotTemplate>>(UserTemplatesKey) ?? [];
        _overrides = storage.Get<List<AutopilotTemplateOverride>>(OverridesKey) ?? [];
    }

    // Raised when a user template or an override changes, so the surface re-renders its template list.
    public event Action? Changed;

    // The combined template list the operator picks from: each of `registrations` as a Plugin-origin
    // template with its override applied (if any), followed by the operator's own User templates. Registrations are
    // passed in rather than held here because they live in the host's in-memory registry, not in this store.
    public IReadOnlyList<AutopilotTemplate> List(IReadOnlyList<RegisteredAutopilotTemplate> registrations)
    {
        var combined = new List<AutopilotTemplate>(registrations.Count + _userTemplates.Count);
        foreach (var registration in registrations)
        {
            combined.Add(_ApplyOverride(AutopilotTemplate.ForPlugin(registration.OwnerPluginId, registration.Template)));
        }

        combined.AddRange(_userTemplates);
        return combined;
    }

    // Adds a new User template or replaces an existing one with the same id. Refuses anything that is not a User template — Plugin/Builtin edits go through `UpsertOverride`.
    public void UpsertUserTemplate(AutopilotTemplate template)
    {
        if (template.Origin != AutopilotTemplateOrigin.User)
        {
            throw new ArgumentException($"Only User-origin templates can be stored directly; '{template.Origin}' templates are edited through an override.", nameof(template));
        }

        var index = _userTemplates.FindIndex(existing => existing.Id == template.Id);
        if (index >= 0)
        {
            _userTemplates[index] = template;
        }
        else
        {
            _userTemplates.Add(template);
        }

        _SaveUserTemplates();
    }

    // Deletes the operator's own template with `id`. A no-op for any other id — only User templates live here, so a Plugin/Builtin id is never removed.
    public void DeleteUserTemplate(string id)
    {
        var index = _userTemplates.FindIndex(existing => existing.Id == id);
        if (index >= 0)
        {
            _userTemplates.RemoveAt(index);
            _SaveUserTemplates();
        }
    }

    // Records the operator's edit of a Plugin/Builtin template, or replaces an existing edit of the same template.
    public void UpsertOverride(AutopilotTemplateOverride templateOverride)
    {
        var index = _overrides.FindIndex(existing => existing.Id == templateOverride.Id);
        if (index >= 0)
        {
            _overrides[index] = templateOverride;
        }
        else
        {
            _overrides.Add(templateOverride);
        }

        _SaveOverrides();
    }

    // Resets a Plugin/Builtin template to its registered default by dropping the operator's override — the registration then shows through unchanged. A no-op when there is no override.
    public void ResetOverride(string id)
    {
        var index = _overrides.FindIndex(existing => existing.Id == id);
        if (index >= 0)
        {
            _overrides.RemoveAt(index);
            _SaveOverrides();
        }
    }

    // Applies the operator's override to a registration-derived template when one exists; the registration stays the
    // source, so resetting the override (dropping it) brings the original back.
    private AutopilotTemplate _ApplyOverride(AutopilotTemplate template)
    {
        var templateOverride = _overrides.FirstOrDefault(existing => existing.Id == template.Id);
        return templateOverride is null
            ? template
            : template with
            {
                Name = templateOverride.Name,
                Body = templateOverride.Body,
                RequiredPlaceholders = templateOverride.RequiredPlaceholders,
            };
    }

    private void _SaveUserTemplates()
    {
        _storage.Set(UserTemplatesKey, _userTemplates);
        Changed?.Invoke();
    }

    private void _SaveOverrides()
    {
        _storage.Set(OverridesKey, _overrides);
        Changed?.Invoke();
    }
}
