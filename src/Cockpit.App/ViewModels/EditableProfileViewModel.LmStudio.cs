using Cockpit.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

public partial class EditableProfileViewModel
{
    public IReadOnlyList<string> LmStudioToggleChoices { get; } = ["LM Studio default", "On", "Off"];
    public IReadOnlyList<string> LmStudioCacheChoices { get; } = ["LM Studio default", .. LmStudioLoadOptions.CacheQuantizations];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(LmStudioLoadOptionsError))]
    private bool _manageLmStudioLoadOptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(LmStudioLoadOptionsError))]
    private double? _lmStudioGpuOffloadRatio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(LmStudioLoadOptionsError))]
    private int? _lmStudioParallel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(LmStudioLoadOptionsError))]
    private string _lmStudioFlashAttention = "LM Studio default";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(LmStudioLoadOptionsError))]
    private string _lmStudioKCacheQuantization = "LM Studio default";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(LmStudioLoadOptionsError))]
    private string _lmStudioVCacheQuantization = "LM Studio default";

    [ObservableProperty]
    private string _lmStudioOffloadKvCacheToGpu = "LM Studio default";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(LmStudioLoadOptionsError))]
    private string _lmStudioDraftMtp = "LM Studio default";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(LmStudioLoadOptionsError))]
    private string _lmStudioDraftModel = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(LmStudioLoadOptionsError))]
    private int? _lmStudioDraftMaxTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(LmStudioLoadOptionsError))]
    private int? _lmStudioDraftMinTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(LmStudioLoadOptionsError))]
    private double? _lmStudioDraftMinContinueProbability;

    [ObservableProperty]
    private string _lmStudioThinking = "LM Studio default";

    public string? LmStudioLoadOptionsError => _LmStudioLoadOptions()?.ValidationError;

    private LmStudioLoadOptions? _LmStudioLoadOptions() => !ManageLmStudioLoadOptions ? null : new(
        LmStudioGpuOffloadRatio,
        LmStudioParallel,
        _ToggleValue(LmStudioFlashAttention),
        _CacheValue(LmStudioKCacheQuantization),
        _CacheValue(LmStudioVCacheQuantization),
        _ToggleValue(LmStudioOffloadKvCacheToGpu),
        _ToggleValue(LmStudioDraftMtp),
        string.IsNullOrWhiteSpace(LmStudioDraftModel) ? null : LmStudioDraftModel.Trim(),
        LmStudioDraftMaxTokens,
        LmStudioDraftMinTokens,
        LmStudioDraftMinContinueProbability);

    private void _InitializeLmStudioOptions(LmStudioConfig? config)
    {
        ManageLmStudioLoadOptions = config?.LoadOptions is not null;
        LmStudioThinking = _ToggleChoice(config?.EnableThinking);
        if (config?.LoadOptions is not { } options)
        {
            return;
        }

        LmStudioGpuOffloadRatio = options.GpuOffloadRatio;
        LmStudioParallel = options.Parallel;
        LmStudioFlashAttention = _ToggleChoice(options.FlashAttention);
        LmStudioKCacheQuantization = options.KCacheQuantization ?? "LM Studio default";
        LmStudioVCacheQuantization = options.VCacheQuantization ?? "LM Studio default";
        LmStudioOffloadKvCacheToGpu = _ToggleChoice(options.OffloadKvCacheToGpu);
        LmStudioDraftMtp = _ToggleChoice(options.DraftMtp);
        LmStudioDraftModel = options.DraftModel ?? string.Empty;
        LmStudioDraftMaxTokens = options.DraftMaxTokens;
        LmStudioDraftMinTokens = options.DraftMinTokens;
        LmStudioDraftMinContinueProbability = options.DraftMinContinueProbability;
    }

    private static bool? _ToggleValue(string? value) => value switch { "On" => true, "Off" => false, _ => null };
    private static string _ToggleChoice(bool? value) => value switch { true => "On", false => "Off", _ => "LM Studio default" };
    private static string? _CacheValue(string? value) => string.IsNullOrWhiteSpace(value) || value == "LM Studio default" ? null : value;
}
