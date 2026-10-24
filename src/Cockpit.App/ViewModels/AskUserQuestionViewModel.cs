using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using Cockpit.Plugins.Abstractions.Channels;

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

    // Reads the `questions` array out of an AskUserQuestion tool input, through the reading a chat channel gets too
    // (AC-1553). Empty for any other tool input, so a row only renders as a question card when it carries one.
    public static IReadOnlyList<AskUserQuestionViewModel> Parse(string? inputJson) =>
        [.. StructuredQuestionJson.Parse(inputJson).Select(question => new AskUserQuestionViewModel(
            question.Question,
            question.Header ?? string.Empty,
            question.MultiSelect,
            question.AllowOther,
            [.. question.Options.Select(option => new AskUserQuestionOptionViewModel(option.Label, option.Description ?? string.Empty))]))];
}
