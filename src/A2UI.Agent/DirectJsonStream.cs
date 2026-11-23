using System.Text;
using System.Text.Json;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>A chunk delta containing text, validated messages, or a recoverable block error.</summary>
/// <param name="Text">Conversational text.</param>
/// <param name="Messages">Messages committed by this chunk.</param>
/// <param name="Error">Diagnostic for a rejected block when recovery was enabled.</param>
public sealed record StreamPart(string? Text = null, IReadOnlyList<WireMessage>? Messages = null, A2uiDiagnostic? Error = null);

/// <summary>Incrementally processes a single Direct JSON response. Callers serialize reads and delivery.</summary>
public sealed class DirectJsonStream
{
    private const string Open = "<a2ui-json>";
    private const string Close = "</a2ui-json>";
    private readonly RendererState _state;
    private readonly HashSet<string> _keys;
    private readonly bool _wrapped;
    private readonly bool _recover;
    private readonly int _limit;
    private long _consumed;
    private string _tail = "";
    private bool _startedText;
    private bool _completed;
    private bool _skipping;
    private bool _inString;
    private bool _escaped;
    private readonly StringBuilder _body = new();
    private readonly HashSet<string> _surfaces = new(StringComparer.Ordinal);
    private readonly List<string> _emitted = [];

    /// <summary>Creates a stream against an existing peer mirror.</summary>
    /// <param name="state">The mirror used across turns.</param>
    /// <param name="progressiveKeys">Property names eligible for partial string emission outside components.</param>
    /// <param name="wrapped">Whether UI is framed by sentinel tags.</param>
    /// <param name="continueAfterRejectedBlock">Whether to emit error parts and resume after the closing tag.</param>
    /// <param name="maxCharacters">Maximum UTF-16 code units accepted per response.</param>
    public DirectJsonStream(RendererState state, IEnumerable<string>? progressiveKeys = null, bool wrapped = true,
        bool continueAfterRejectedBlock = false, int maxCharacters = 1_048_576)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _keys = new(progressiveKeys ?? [], StringComparer.Ordinal);
        _wrapped = wrapped;
        _recover = continueAfterRejectedBlock;
        _limit = maxCharacters;
        InBlock = !wrapped;
    }

    /// <summary>Whether the stream is currently inside a payload block.</summary>
    public bool InBlock { get; private set; }

    /// <summary>Consumes one chunk synchronously. Returning the delta transfers delivery responsibility to the host.</summary>
    /// <param name="chunk">The next segment of model output.</param>
    /// <param name="parts">Ordered deltas, including the accepted prefix before an error.</param>
    /// <param name="final">Whether no more input will arrive.</param>
    /// <param name="cancellationToken">Cancels processing at a chunk boundary.</param>
    /// <returns>The terminal diagnostic, or null on success.</returns>
    public A2uiDiagnostic? Read(string chunk, out IReadOnlyList<StreamPart> parts, bool final = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        cancellationToken.ThrowIfCancellationRequested();
        if (_completed)
            throw new InvalidOperationException("The response stream has ended.");
        var output = new List<StreamPart>();
        parts = output.AsReadOnly();
        _consumed += chunk.Length;
        if (_consumed > _limit)
        {
            _completed = true;
            return new("ResourceLimit", "", "The response exceeds the configured size limit.");
        }
        var input = _tail + chunk;
        _tail = "";
        var start = 0;
        for (var i = 0; i < input.Length; i++)
        {
            if (!InBlock)
            {
                if (input[i] != '<') continue;
                if (input.AsSpan(i).StartsWith(Open, StringComparison.Ordinal))
                {
                    Text(input[start..i], true);
                    _startedText = false;
                    InBlock = true;
                    i += Open.Length - 1;
                    start = i + 1;
                }
                else if (!final && Open.AsSpan().StartsWith(input.AsSpan(i), StringComparison.Ordinal))
                {
                    Text(input[start..i], false);
                    _tail += input[i..];
                    start = input.Length;
                    break;
                }
                continue;
            }
            if (!_inString && _wrapped && input[i] == '<')
            {
                if (input.AsSpan(i).StartsWith(Close, StringComparison.Ordinal))
                {
                    var error = Flush(true);
                    InBlock = false;
                    Reset();
                    if (error is not null) return error;
                    i += Close.Length - 1;
                    start = i + 1;
                    continue;
                }
                if (!final && Close.AsSpan().StartsWith(input.AsSpan(i), StringComparison.Ordinal))
                {
                    _tail = input[i..];
                    break;
                }
            }
            var c = input[i];
            if (_escaped) _escaped = false;
            else if (_inString && c == '\\') _escaped = true;
            else if (c == '"') _inString = !_inString;
            if (!_skipping) _body.Append(c);
        }

        if (InBlock)
        {
            if (final && _wrapped)
            {
                _completed = true;
                InBlock = false;
                return Reject(new("ParseError", "", "Missing closing Direct JSON tag."));
            }
            if (Flush(final) is { } error) return error;
            if (final) InBlock = false;
        }
        else if (start < input.Length)
            Text(input[start..], final);
        _completed = final;
        return null;

        void Text(string value, bool finish)
        {
            if (!_startedText) value = value.TrimStart();
            var text = value.TrimEnd();
            if (!finish)
            {
                if (text.Length == value.Length) text = text.TrimEnd((char)96).TrimEnd();
                _tail = value[text.Length..];
            }
            if (text.Length == 0 || text.All(c => c == (char)96) && !_startedText) return;
            output.Add(new(Text: text));
            _startedText = true;
        }

        A2uiDiagnostic? Flush(bool closed)
        {
            if (_skipping) return null;
            var raw = _body.ToString().Trim().Trim((char)96).Trim();
            if (raw.Length == 0 && !closed) return null;
            var candidates = new List<WireMessage>();
            var diagnostic = closed ? DecodeAll(raw, candidates) : Preview(raw, candidates);
            if (diagnostic is not null) return Reject(diagnostic);
            var delta = new List<WireMessage>();
            for (var n = 0; n < candidates.Count; n++)
                if (n >= _emitted.Count || candidates[n].Envelope.GetRawText() != _emitted[n])
                    delta.Add(candidates[n]);
            if (delta.Count == 0 && !closed) return null;
            var problem = _state.Prepare(delta, out var turn, _surfaces, closed, default);
            if (problem is not null)
            {
                if (!closed)
                {
                    var prefix = new List<WireMessage>();
                    foreach (var message in delta)
                    {
                        if (_state.Prepare([message], out var accepted, null, false, default) is not null)
                            break;
                        using (accepted)
                        {
                            accepted!.Commit();
                            accepted.Acknowledge(message);
                        }
                        prefix.Add(message);
                    }
                    if (prefix.Count > 0)
                        output.Add(new(Messages: prefix.AsReadOnly()));
                }
                return Reject(problem with { Code = "ValidationError" });
            }
            using (turn)
            {
                turn!.Commit();
                foreach (var message in delta)
                {
                    if (message.Payload.TryGetProperty("surfaceId", out var id)) _surfaces.Add(id.GetString()!);
                    turn.Acknowledge(message);
                }
            }
            if (delta.Count != 0)
            {
                output.Add(new(Messages: delta.AsReadOnly()));
                _emitted.Clear();
                _emitted.AddRange(candidates.Select(m => m.Envelope.GetRawText()));
            }
            return null;
        }

        A2uiDiagnostic? Reject(A2uiDiagnostic error)
        {
            if (!_recover)
            {
                _completed = true;
                return error;
            }
            if (!_skipping) output.Add(new(Error: error));
            _skipping = true;
            return null;
        }
    }

    private void Reset()
    {
        _body.Clear();
        _surfaces.Clear();
        _emitted.Clear();
        _inString = _escaped = _skipping = false;
    }

    private static A2uiDiagnostic? DecodeAll(string raw, List<WireMessage> result)
    {
        var error = DirectJson.Decode(raw, out var messages);
        result.AddRange(messages);
        return error;
    }

    private A2uiDiagnostic? Preview(string raw, List<WireMessage> result)
    {
        var braces = new Stack<(char Token, int Start, string? Key)>();
        var stringStart = -1;
        var escaped = false;
        var valueStart = -1;
        var componentStart = -1;
        string? key = null;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (stringStart >= 0)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"')
                {
                    key = raw[(stringStart + 1)..i];
                    stringStart = -1;
                }
                continue;
            }
            if (c == '"') { stringStart = i; continue; }
            if (c is '[' or '{')
            {
                if (c == '{' && (braces.Count == 0 || braces.Count == 1 && braces.Peek().Token == '['))
                    valueStart = i;
                if (c == '{' && braces.TryPeek(out var parent) && parent.Token == '[' && parent.Key == "components")
                    componentStart = i;
                braces.Push((c, i, key));
                key = null;
            }
            else if (c is ']' or '}')
            {
                if (!braces.TryPop(out var pop) || pop.Token != (c == ']' ? '[' : '{')) return null;
                if (componentStart == pop.Start) componentStart = -1;
                if (valueStart == pop.Start)
                {
                    var diagnostic = DecodeAll(raw[valueStart..(i + 1)], result);
                    if (diagnostic is not null) return diagnostic;
                    valueStart = -1;
                }
            }
            else if (c == ',') key = null;
        }
        if (valueStart < 0) return null;
        var end = raw.Length;
        if (componentStart >= 0)
        {
            end = componentStart;
            while (braces.Peek().Start >= componentStart) braces.Pop();
            stringStart = -1;
        }
        var slice = raw[valueStart..end].TrimEnd().TrimEnd(',').TrimEnd();
        if (stringStart >= 0)
        {
            var partial = raw[(stringStart + 1)..];
            var prefix = raw[..stringStart].TrimEnd();
            if (escaped || !prefix.EndsWith(':') || key is null || !_keys.Contains(key) ||
                partial.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                partial.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                partial.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
            slice += '"';
        }
        var healed = slice + string.Concat(braces.TakeWhile(b => b.Start >= valueStart).Select(b => b.Token == '[' ? ']' : '}'));
        if (DirectJson.Decode(healed, out var preview) is not null || preview.Count != 1) return null;
        var message = preview[0];
        if (message.Kind == "updateDataModel" || message.Kind == "updateComponents" &&
            message.Payload.GetProperty("components").GetArrayLength() > 0 && (componentStart >= 0 ||
                !braces.Any(b => b.Token == '{' && b.Key == "components")))
            result.Add(message);
        return null;
    }
}
