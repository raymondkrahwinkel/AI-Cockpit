namespace Cockpit.Core.Abstractions.Voice;

/// <summary>The base URL + model a local-LLM call should use, once auto-detect (or the manual fallback) has decided.</summary>
public readonly record struct LocalLlmEndpoint(string BaseUrl, string Model);
