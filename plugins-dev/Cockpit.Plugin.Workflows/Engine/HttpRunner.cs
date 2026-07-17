using System.Text;
using Cockpit.Plugin.Workflows.Model;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// Calls something over HTTP and carries the answer on. The status and the body both flow, so the next step can
/// decide on either — <c>{= status != '200' }</c> is a decision this makes possible, and it is the whole reason the
/// status is not merely logged.
/// <para>
/// A status the server calls an error fails the step. A flow that carries on with a 500 in hand, having reported
/// green, is exactly the failure that is invisible until the day it matters.
/// </para>
/// </summary>
internal sealed class HttpRunner : IStepRunner
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string TypeId => "cockpit.http";

    // Arbitrary egress: an http step can POST any data to any URL, so it is dangerous (asked every time, never
    // remembered), like the chat steps that also send data out.
    public ConsentRisk? RequiredConsent => ConsentRisk.Dangerous;

    public string ConsentAction(StepContext context)
    {
        var method = context.Node.Parameters.GetValueOrDefault("Method")?.Trim();
        var url = context.Resolve(context.Node.Parameters.GetValueOrDefault("URL")).Text.Trim();
        var body = context.Resolve(context.Node.Parameters.GetValueOrDefault("Body")).Text;
        var line = $"{(string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant())} {url}";
        // The body is what actually goes out, so the operator must see it — a URL alone hides the data being sent.
        return body.Length == 0 ? line : $"{line}\n{body}";
    }

    public async Task<StepOutcome> RunAsync(StepContext context, CancellationToken cancellationToken)
    {
        var url = context.Resolve(context.Node.Parameters.GetValueOrDefault("URL")).Text.Trim();
        if (url.Length == 0)
        {
            throw new InvalidOperationException("This step has no URL. Open it and write one.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"'{url}' is not an http(s) address.");
        }

        var method = context.Node.Parameters.GetValueOrDefault("Method")?.Trim();
        var body = context.Resolve(context.Node.Parameters.GetValueOrDefault("Body")).Text;

        using var request = new HttpRequestMessage(
            new HttpMethod(string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant()),
            target);

        if (body.Length > 0)
        {
            // JSON unless it plainly is not: a body that starts with a brace is the case this cockpit meets, and
            // asking for a content-type field to send one would be a form with a lonely field on it.
            var isJson = body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[');
            request.Content = new StringContent(body, Encoding.UTF8, isJson ? "application/json" : "text/plain");
        }

        using var response = await Http.SendAsync(request, cancellationToken);
        var answer = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{target.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}: {answer.Trim()}");
        }

        return new StepOutcome(
            [
                WorkflowItem.Of(new Dictionary<string, string>
                {
                    ["status"] = ((int)response.StatusCode).ToString(),
                    ["body"] = answer,
                }),
            ],
            $"{(int)response.StatusCode} from {target.Host}");
    }
}
