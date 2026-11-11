using System.Text.Json.Serialization;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.BackendApi;

internal sealed record AssistantServerOptions(
    [property: JsonRequired] bool IsEnabled,
    [property: JsonRequired] ReadingLevel ReadingLevel,
    [property: JsonRequired] double ContextCompactionPercent,
    [property: JsonRequired] string? SpeechProfileLabel,
    [property: JsonRequired] bool ConsentBypassAll,
    [property: JsonRequired] IReadOnlyList<string> ConsentBypassSources,
    [property: JsonRequired] IReadOnlyList<string> ConsentBypassDangerousSources);
