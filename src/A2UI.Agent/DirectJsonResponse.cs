using A2UI.Core;

namespace A2UI.Agent;

/// <summary>An ordered conversational text segment or compiled UI block.</summary>
/// <param name="Text">The conversational text, or null for a UI block.</param>
/// <param name="Messages">The compiled UI messages, or null for a text segment.</param>
public sealed record DirectJsonResponsePart(string? Text, IReadOnlyList<WireMessage>? Messages);

public static partial class DirectJson
{
    /// <summary>Compiles one complete model response and prepares its state mutations atomically.</summary>
    /// <param name="response">The complete model response.</param>
    /// <param name="state">The peer's mirror across turns.</param>
    /// <param name="parts">Ordered output on success, or the decoded prefix for diagnostics on failure.</param>
    /// <param name="turn">A validated transaction on success. Commit it before emitting UI messages.</param>
    /// <param name="wrapped">Whether the model used sentinel tags around blocks.</param>
    /// <param name="cancellationToken">Cancels processing before commit.</param>
    /// <returns>A parse or validation diagnostic, or null on success.</returns>
    public static A2uiDiagnostic? ParseResponse(string response, RendererState state,
        out IReadOnlyList<DirectJsonResponsePart> parts, out RendererTurn? turn, bool wrapped = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        var decoded = new List<DirectJsonResponsePart>();
        var messages = new List<WireMessage>();
        parts = decoded.AsReadOnly();
        turn = null;
        foreach (var part in wrapped ? Unwrap(response) : [new DirectJsonPart(response, true)])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!part.IsPayload)
            {
                decoded.Add(new(part.Content, null));
                continue;
            }
            if (!part.IsFinal)
                return new("ParseError", "", "Missing closing Direct JSON tag.");
            if (Decode(part.Content, out var block) is { } error)
                return error;
            messages.AddRange(block);
            decoded.Add(new(null, block));
        }
        var issue = state.Prepare(messages, out turn, cancellationToken);
        return issue is null ? null : issue with { Code = "ValidationError" };
    }
}
