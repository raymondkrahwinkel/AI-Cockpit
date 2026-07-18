namespace Cockpit.Plugins.Abstractions.StatusBar;

/// <summary>One labelled fact about a <see cref="SupervisedActivity"/>, shown verbatim in the status-bar panel — e.g. ("cluster", "prod"), ("local", "8080"), ("pod", "nginx-1:80").</summary>
public sealed record ActivityDetail(string Label, string Value);
