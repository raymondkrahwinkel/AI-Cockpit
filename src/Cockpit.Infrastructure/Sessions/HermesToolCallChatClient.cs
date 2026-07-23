using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// A <see cref="DelegatingChatClient"/> that rescues the Hermes/XML text tool-calls some local models emit as plain
/// assistant text instead of the OpenAI structured <c>tool_calls</c> field (AC-192). qwen-coder via Ollama, for one,
/// writes <c>&lt;function=read_file&gt;&lt;parameter=path&gt;/x&lt;/parameter&gt;&lt;/function&gt;&lt;/tool_call&gt;</c>
/// straight into its content, where <c>UseFunctionInvocation</c> never sees it — so the call is never run, the run
/// hangs, and the turn "succeeds" with the nonsense text as its answer.
/// <para>
/// This client sits between <c>UseFunctionInvocation</c> (the outer layer) and the model client (the inner). It buffers
/// the streamed text and, whenever a complete <c>&lt;function=NAME&gt;…&lt;/function&gt;</c> block (optionally trailed by
/// the <c>&lt;/tool_call&gt;</c> wrapper) has arrived, replaces it with a synthesised <see cref="FunctionCallContent"/>
/// carrying a unique call id and the <c>&lt;parameter=key&gt;value&lt;/parameter&gt;</c> pairs as its arguments — the
/// exact shape <c>UseFunctionInvocation</c> recognises and executes through the gated tools. Plain text flows through
/// untouched, and a model that already emits a real <see cref="FunctionCallContent"/> is left completely alone: this
/// client only ever rewrites Hermes text in the content field.
/// </para>
/// </summary>
internal sealed class HermesToolCallChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    private const string FunctionOpen = "<function=";
    private const string FunctionClose = "</function>";
    private const string ToolCallOpen = "<tool_call>";
    private const string ToolCallClose = "</tool_call>";

    // <function=NAME …> — the name is everything up to the first '>', whitespace trimmed.
    private static readonly Regex FunctionNameRegex =
        new(@"^<function=\s*(?<name>[^>]*?)\s*>", RegexOptions.Compiled | RegexOptions.Singleline);

    // <parameter=KEY>VALUE</parameter> — repeated 0..n inside a block.
    private static readonly Regex ParameterRegex =
        new(@"<parameter=\s*(?<key>[^>]*?)\s*>(?<value>.*?)</parameter>", RegexOptions.Compiled | RegexOptions.Singleline);

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Buffer state is local to this call: every turn — and every follow-up round UseFunctionInvocation drives after
        // running a tool — is a fresh GetStreamingResponseAsync, so a Hermes block never straddles two of them.
        var buffer = new StringBuilder();
        ChatResponseUpdate? template = null;

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            template = update;

            // Any non-text content (a real structured FunctionCallContent, usage, …) is not ours to touch: flush the
            // safe buffered text first so ordering holds, then pass it straight through.
            var passthrough = update.Contents.Where(content => content is not TextContent).ToList();

            var text = string.Concat(update.Contents.OfType<TextContent>().Select(content => content.Text));
            if (text.Length > 0)
            {
                buffer.Append(text);
            }

            foreach (var content in _DrainCompleteBlocks(buffer, flushAll: false))
            {
                yield return _CloneWith(template, content);
            }

            if (passthrough.Count > 0)
            {
                yield return _CloneWith(template, passthrough);
            }
        }

        // Stream ended: flush whatever is left as text. An incomplete/malformed block stays as text on purpose — the
        // driver's no-progress guard turns a surviving marker with no tool activity into a visible error (AC-192),
        // rather than the silent hang a half-emitted tool-call used to cause.
        foreach (var content in _DrainCompleteBlocks(buffer, flushAll: true))
        {
            yield return _CloneWith(template, content);
        }
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        // The non-streaming path is a belt-and-braces mirror of the streaming rewrite: rebuild each message's contents,
        // expanding any Hermes text into the same synthesised FunctionCallContent, and leaving everything else as-is.
        foreach (var message in response.Messages)
        {
            if (message.Contents.Any(content => content is TextContent))
            {
                message.Contents = _RewriteMessageContents(message.Contents);
            }
        }

        return response;
    }

    private static List<AIContent> _RewriteMessageContents(IList<AIContent> contents)
    {
        var rewritten = new List<AIContent>(contents.Count);
        foreach (var content in contents)
        {
            if (content is TextContent text)
            {
                rewritten.AddRange(_ParseCompleteText(text.Text));
            }
            else
            {
                rewritten.Add(content);
            }
        }

        return rewritten;
    }

    private static List<AIContent> _ParseCompleteText(string text)
    {
        var buffer = new StringBuilder(text);
        return _DrainCompleteBlocks(buffer, flushAll: true);
    }

    // Pulls every complete Hermes block out of <paramref name="buffer"/>, in order, returning the emissions (plain
    // TextContent for the text between/around blocks, a synthesised FunctionCallContent per block) and leaving the
    // still-incomplete tail in the buffer. With <paramref name="flushAll"/> the tail is emitted as text too (end of
    // stream); otherwise a tail that could be the start of a marker split across updates is held back.
    private static List<AIContent> _DrainCompleteBlocks(StringBuilder buffer, bool flushAll)
    {
        var emissions = new List<AIContent>();
        var work = buffer.ToString();
        var consumed = 0;

        while (true)
        {
            var open = work.IndexOf(FunctionOpen, consumed, StringComparison.Ordinal);
            var wrapperOpen = work.IndexOf(ToolCallOpen, consumed, StringComparison.Ordinal);
            var wrapperClose = work.IndexOf(ToolCallClose, consumed, StringComparison.Ordinal);
            var wrapper = _Earliest(wrapperOpen, wrapperClose);

            // A complete <tool_call>/</tool_call> wrapper token before any function block is Hermes scaffolding, not
            // model output — emit the text before it and skip the token.
            if (wrapper >= 0 && (open < 0 || wrapper < open))
            {
                if (wrapper > consumed)
                {
                    // Whitespace between a block's </function> and its </tool_call> wrapper is Hermes formatting, not
                    // model output — drop it so it does not leak as a stray text delta; keep any real text.
                    var preceding = work.Substring(consumed, wrapper - consumed);
                    if (!string.IsNullOrWhiteSpace(preceding))
                    {
                        emissions.Add(new TextContent(preceding));
                    }
                }

                consumed = wrapper + (_StartsWith(work, wrapper, ToolCallClose) ? ToolCallClose.Length : ToolCallOpen.Length);
                continue;
            }

            if (open < 0)
            {
                break;
            }

            var close = work.IndexOf(FunctionClose, open, StringComparison.Ordinal);
            if (close < 0)
            {
                // The block has opened but not closed yet — stop here and let the tail be held for the next update.
                break;
            }

            if (open > consumed)
            {
                emissions.Add(new TextContent(work.Substring(consumed, open - consumed)));
            }

            var blockEnd = close + FunctionClose.Length;
            emissions.Add(_ParseBlock(work.Substring(open, blockEnd - open)));
            consumed = blockEnd;
        }

        var remainder = work.Substring(consumed);
        buffer.Clear();

        if (flushAll)
        {
            if (remainder.Length > 0)
            {
                emissions.Add(new TextContent(remainder));
            }

            return emissions;
        }

        // Mid-stream: emit the safe prefix as text and hold back a tail that could still become a marker.
        var safe = _SafeTextLength(remainder);
        if (safe > 0)
        {
            emissions.Add(new TextContent(remainder[..safe]));
        }

        buffer.Append(remainder, safe, remainder.Length - safe);
        return emissions;
    }

    // How much of a not-yet-final remainder is safe to emit as text now: everything before an in-progress
    // '<function=' block, or — with no marker present — everything but a trailing partial that could be the start of a
    // marker split across the next update (…<fun | ction=…).
    private static int _SafeTextLength(string remainder)
    {
        var open = remainder.IndexOf(FunctionOpen, StringComparison.Ordinal);
        if (open >= 0)
        {
            return open;
        }

        var hold = Math.Max(
            _PartialMarkerSuffix(remainder, FunctionOpen),
            Math.Max(_PartialMarkerSuffix(remainder, ToolCallOpen), _PartialMarkerSuffix(remainder, ToolCallClose)));
        return remainder.Length - hold;
    }

    // The length of the longest suffix of <paramref name="text"/> that is a strict prefix of <paramref name="marker"/>
    // — a marker cut mid-way by the streaming chunk boundary. 0 when none.
    private static int _PartialMarkerSuffix(string text, string marker)
    {
        var max = Math.Min(text.Length, marker.Length - 1);
        for (var length = max; length > 0; length--)
        {
            if (string.CompareOrdinal(text, text.Length - length, marker, 0, length) == 0)
            {
                return length;
            }
        }

        return 0;
    }

    private static FunctionCallContent _ParseBlock(string block)
    {
        var nameMatch = FunctionNameRegex.Match(block);
        var name = nameMatch.Success ? nameMatch.Groups["name"].Value.Trim() : string.Empty;

        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (Match parameter in ParameterRegex.Matches(block))
        {
            var key = parameter.Groups["key"].Value.Trim();
            if (key.Length == 0)
            {
                continue;
            }

            // Values ride as trimmed strings — a local model emits no type information in the XML, so the tool's own
            // argument binding coerces from string as needed.
            arguments[key] = parameter.Groups["value"].Value.Trim();
        }

        // A unique call id lets UseFunctionInvocation match the tool result back to this call.
        return new FunctionCallContent(Guid.NewGuid().ToString("N"), name, arguments);
    }

    private static int _Earliest(int a, int b) => (a, b) switch
    {
        (< 0, _) => b,
        (_, < 0) => a,
        _ => Math.Min(a, b),
    };

    private static bool _StartsWith(string text, int index, string value) =>
        index + value.Length <= text.Length && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static ChatResponseUpdate _CloneWith(ChatResponseUpdate? template, AIContent content) =>
        _CloneWith(template, new List<AIContent> { content });

    private static ChatResponseUpdate _CloneWith(ChatResponseUpdate? template, IList<AIContent> contents) =>
        new(template?.Role ?? ChatRole.Assistant, contents)
        {
            ResponseId = template?.ResponseId,
            MessageId = template?.MessageId,
            CreatedAt = template?.CreatedAt,
            ModelId = template?.ModelId,
        };
}
