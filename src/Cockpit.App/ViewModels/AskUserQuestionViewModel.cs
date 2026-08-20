using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;

namespace Cockpit.App.ViewModels;

// One question from an AskUserQuestion tool call (AC-715) — the clarifying-question tool that arrives over the
// same permission callback as a tool approval, with its choices already written by the agent. The operator picks
// a label (or types their own under "Other", the fallback the SDK guarantees); the chosen label is what goes back.
public partial class AskUserQuestionViewModel : ViewModelBase
{
    public string Question { get; }

    // A short caption the agent supplies for the question (max 12 characters per the SDK), or empty.
    public string Header { get; }

    public bool HasHeader => Header.Length > 0;

    public bool MultiSelect { get; }

    // Whether the "Other, namely…" row shows at all (AC-955) — on for a native AskUserQuestion, whose SDK
    // guarantees the fallback and carries no field to turn it off; ask_structured_question sets it explicitly.
    public bool AllowOther { get; }

    public IReadOnlyList<AskUserQuestionOptionViewModel> Options { get; }

    // Raised whenever the answer changes, so the owning row can re-evaluate whether Send is allowed.
    public Action? AnswerChanged { get; set; }

    // The operator's own wording, used only while "Other" is the active choice.
    [ObservableProperty]
    private string _otherText = string.Empty;

    [ObservableProperty]
    private bool _isOtherSelected;

    // True once the operator has sent their answers: the block stays on screen with the choice it made,
    // rather than collapsing away the question the agent asked (Raymond's call on AC-715).
    [ObservableProperty]
    private bool _isAnswered;

    // What goes back to the agent (AC-955). Single-select keeps "Other" and the ticked pick mutually
    // exclusive, the same rule the SDK's own reference handler applies. Multi-select does not: an ordinary
    // answer can combine picks with typed text, so "Other" joins the ticked labels instead of replacing them.
    public string Answer => MultiSelect
        ? string.Join(", ", _SelectedLabels())
        : IsOtherSelected
            ? OtherText.Trim()
            : string.Join(", ", Options.Where(option => option.IsSelected).Select(option => option.Label));

    private IEnumerable<string> _SelectedLabels()
    {
        foreach (var option in Options.Where(option => option.IsSelected))
        {
            yield return option.Label;
        }

        if (IsOtherSelected && OtherText.Trim() is { Length: > 0 } otherText)
        {
            yield return otherText;
        }
    }

    public bool HasAnswer => Answer.Length > 0;

    public MaterialIconKind OtherIconKind => MultiSelect
        ? (IsOtherSelected ? MaterialIconKind.CheckboxMarked : MaterialIconKind.CheckboxBlankOutline)
        : (IsOtherSelected ? MaterialIconKind.RadioboxMarked : MaterialIconKind.RadioboxBlank);

    public AskUserQuestionViewModel(
        string question, string header, bool multiSelect, bool allowOther, IReadOnlyList<AskUserQuestionOptionViewModel> options)
    {
        Question = question;
        Header = header;
        MultiSelect = multiSelect;
        AllowOther = allowOther;
        Options = options;

        foreach (var option in options)
        {
            option.MultiSelect = multiSelect;
            option.SelectRequested = () => SelectOption(option);
        }
    }

    partial void OnIsAnsweredChanged(bool value)
    {
        foreach (var option in Options)
        {
            option.IsSelectable = !value;
        }
    }

    // Picks an option: a toggle when the question allows several, otherwise the one choice, which clears the
    // siblings and the "Other" box. Selecting nothing at all is a valid intermediate state — Send stays disabled.
    [RelayCommand]
    private void SelectOption(AskUserQuestionOptionViewModel option)
    {
        if (IsAnswered)
        {
            return;
        }

        if (MultiSelect)
        {
            // AC-955: ticking an option leaves "Other" exactly as it was — the two stack for a multi-select
            // question, so picking one more choice must not silently drop a typed answer that stood beside it.
            option.IsSelected = !option.IsSelected;
        }
        else
        {
            foreach (var candidate in Options)
            {
                candidate.IsSelected = ReferenceEquals(candidate, option);
            }

            IsOtherSelected = false;
        }

        _RaiseAnswerChanged();
    }

    // Switches to the free-text fallback. Single-select: exclusive, dropping whatever was ticked, same as the
    // SDK's own reference handler. Multi-select (AC-955): a checkbox like any other — toggles on or off beside
    // whatever options are already ticked, rather than replacing them.
    [RelayCommand]
    private void SelectOther()
    {
        if (IsAnswered)
        {
            return;
        }

        if (MultiSelect)
        {
            IsOtherSelected = !IsOtherSelected;
        }
        else
        {
            foreach (var option in Options)
            {
                option.IsSelected = false;
            }

            IsOtherSelected = true;
        }

        _RaiseAnswerChanged();
    }

    partial void OnOtherTextChanged(string value) => _RaiseAnswerChanged();

    partial void OnIsOtherSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(Answer));
        OnPropertyChanged(nameof(HasAnswer));
        OnPropertyChanged(nameof(OtherIconKind));
    }

    private void _RaiseAnswerChanged()
    {
        OnPropertyChanged(nameof(Answer));
        OnPropertyChanged(nameof(HasAnswer));
        AnswerChanged?.Invoke();
    }

    // Reads the `questions` array out of an AskUserQuestion tool input. Returns an empty list for any other tool
    // input, so a row only renders as a question card when it genuinely carries one; a question without text is
    // dropped rather than shown as a blank block.
    public static IReadOnlyList<AskUserQuestionViewModel> Parse(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("questions", out var questions)
                || questions.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. questions.EnumerateArray()
                .Select(_ParseQuestion)
                .OfType<AskUserQuestionViewModel>()];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static AskUserQuestionViewModel? _ParseQuestion(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || _ReadString(element, "question") is not { Length: > 0 } question)
        {
            return null;
        }

        var multiSelect = element.TryGetProperty("multiSelect", out var multi) && multi.ValueKind == JsonValueKind.True;
        // Defaults to on: a native AskUserQuestion carries no such field and its SDK guarantees the fallback
        // regardless (AC-715); only ask_structured_question's own payload can turn it off explicitly (AC-955).
        var allowOther = !element.TryGetProperty("allowOther", out var other) || other.ValueKind != JsonValueKind.False;
        return new AskUserQuestionViewModel(question, _ReadString(element, "header"), multiSelect, allowOther, _ParseOptions(element));
    }

    private static IReadOnlyList<AskUserQuestionOptionViewModel> _ParseOptions(JsonElement question)
    {
        if (!question.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. options.EnumerateArray()
            .Where(option => option.ValueKind == JsonValueKind.Object)
            .Select(option => (Label: _ReadString(option, "label"), Description: _ReadString(option, "description")))
            .Where(option => option.Label.Length > 0)
            .Select(option => new AskUserQuestionOptionViewModel(option.Label, option.Description))];
    }

    private static string _ReadString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;
}
