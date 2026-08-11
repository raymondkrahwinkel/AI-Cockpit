using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    // What goes back to the agent: the chosen option labels, comma-separated when several are allowed, or the
    // free text typed under "Other". The two are mutually exclusive — the same rule the SDK's own reference
    // handler applies, where a typed response replaces the numbered picks rather than adding to them.
    public string Answer => IsOtherSelected
        ? OtherText.Trim()
        : string.Join(", ", Options.Where(option => option.IsSelected).Select(option => option.Label));

    public bool HasAnswer => Answer.Length > 0;

    public AskUserQuestionViewModel(string question, string header, bool multiSelect, IReadOnlyList<AskUserQuestionOptionViewModel> options)
    {
        Question = question;
        Header = header;
        MultiSelect = multiSelect;
        Options = options;

        foreach (var option in options)
        {
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
            option.IsSelected = !option.IsSelected;
        }
        else
        {
            foreach (var candidate in Options)
            {
                candidate.IsSelected = ReferenceEquals(candidate, option);
            }
        }

        IsOtherSelected = false;
        _RaiseAnswerChanged();
    }

    // Switches to the free-text fallback, dropping whatever was ticked.
    [RelayCommand]
    private void SelectOther()
    {
        if (IsAnswered)
        {
            return;
        }

        foreach (var option in Options)
        {
            option.IsSelected = false;
        }

        IsOtherSelected = true;
        _RaiseAnswerChanged();
    }

    partial void OnOtherTextChanged(string value) => _RaiseAnswerChanged();

    partial void OnIsOtherSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(Answer));
        OnPropertyChanged(nameof(HasAnswer));
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
        return new AskUserQuestionViewModel(question, _ReadString(element, "header"), multiSelect, _ParseOptions(element));
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
