using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.ModelIntelligence;

namespace Cockpit.App;

// AC-1814: Options › Model intelligence over a stand-in source, so the render shows ages, a local table and derived patterns.
internal static class ModelIntelligenceScene
{
    public static OptionsDialog Filled(int width, int height) => _Dialog(width, height, withPatterns: true);

    // Too little data: no patterns yet, which reads as "unknown".
    public static OptionsDialog Unknown(int width, int height) => _Dialog(width, height, withPatterns: false);

    private static OptionsDialog _Dialog(int width, int height, bool withPatterns)
    {
        var cockpit = new CockpitViewModel { ModelIntelligence = new ModelIntelligenceOptionsViewModel(new SceneSource(withPatterns)) };
        cockpit.ModelIntelligence.LoadCommand.Execute(null);
        var dialog = new OptionsDialog { DataContext = cockpit, Width = width, Height = height };
        dialog.SelectCategory("model-intelligence");
        return dialog;
    }

    private sealed class SceneSource(bool withPatterns) : IModelIntelligenceOptions
    {
        public Task<ModelIntelligenceOverview> ReadAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            var from = now.AddDays(-28);
            DerivedPattern[] patterns = withPatterns
                ?
                [
                    new("claude", null, ModelPatternMetric.QuotaDrainPercentPerHour, "Europe/Amsterdam",
                        [new PatternWindow([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], new TimeOnly(14, 0), new TimeOnly(17, 0), 6.4, 2.1, 96)],
                        212, 0.74, from, now.AddHours(-1)),
                    new("claude", null, ModelPatternMetric.LimitHitShare, "Europe/Amsterdam", [], 64, 0.18, from, now.AddHours(-1)),
                ]
                : [];
            return Task.FromResult(new ModelIntelligenceOverview(
                [
                    new ModelDataAge("OpenRouter /api/v1/models", now.AddHours(-3)),
                    new ModelDataAge("claude (provider)", now.AddMinutes(-4)),
                    new ModelDataAge("Own measurements", withPatterns ? now.AddHours(-1) : null),
                ],
                [
                    new LocalModelEntry("qwen3-coder-30b", "mid", ["code", "refactor"], "Fast on the desktop GPU"),
                    new LocalModelEntry("gemma-3-12b", "small", ["summaries"], null),
                ],
                patterns));
        }

        public Task<bool> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task SaveLocalModelsAsync(IReadOnlyList<LocalModelEntry> localModels, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
