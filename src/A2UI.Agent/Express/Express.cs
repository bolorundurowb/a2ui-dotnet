using System.Text.Json;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>A text or raw payload part from an A2UI Express response.</summary>
/// <param name="Content">The text or unwrapped payload.</param>
/// <param name="IsPayload">Whether the content is an Express block.</param>
/// <param name="IsFinal">Whether the closing sentinel was present.</param>
public sealed record ExpressPart(string Content, bool IsPayload = false, bool IsFinal = true);

/// <summary>An ordered conversational text segment or compiled UI block from an Express response.</summary>
/// <param name="Text">The conversational text, or null for a UI block.</param>
/// <param name="Messages">The compiled UI messages, or null for a text segment.</param>
public sealed record ExpressResponsePart(string? Text, IReadOnlyList<WireMessage>? Messages);

/// <summary>
/// Reads and writes the A2UI Express format: a compact notation for components, data and actions that a model
/// writes between <c>&lt;a2ui&gt;</c> tags and that is compiled into A2UI 1.0 messages.
/// </summary>
public static class Express
{
    private const string OpenTag = "<a2ui>";
    private const string CloseTag = "</a2ui>";
    private const string DefaultSurfaceId = "default_surface";

    /// <summary>Splits a complete model response into ordered text and raw Express blocks.</summary>
    /// <param name="response">The complete model response.</param>
    /// <returns>
    /// Ordered parts, including an unterminated final block. A block whose opening tag is missing is recovered
    /// once from its closing tag.
    /// </returns>
    public static IReadOnlyList<ExpressPart> Unwrap(string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return ExpressBlockLexer.Tokenize(response).AsReadOnly();
    }

    /// <summary>Writes text and raw blocks in order, with each block between Express sentinel tags on lines of their own.</summary>
    /// <param name="parts">The parts to wrap. Every payload is closed, whatever its <see cref="ExpressPart.IsFinal"/> value.</param>
    /// <returns>A model response in the Express format.</returns>
    public static string Wrap(IEnumerable<ExpressPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return string.Join("\n", parts.Select(part => part.IsPayload ? OpenTag + "\n" + part.Content + "\n" + CloseTag : part.Content));
    }

    /// <summary>Checks whether a response contains an Express block, by the same rules as <see cref="Unwrap"/>.</summary>
    /// <param name="content">The response text.</param>
    /// <param name="complete">Whether the block must be closed to count.</param>
    /// <returns>Whether the response contains an Express block.</returns>
    public static bool HasFormatContent(string content, bool complete = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Unwrap(content).Any(part => part.IsPayload && (!complete || part.IsFinal));
    }

    /// <summary>Compiles one Express block into A2UI messages and checks them against the catalogs.</summary>
    /// <param name="raw">The Express text. Sentinel tags around it are accepted and ignored.</param>
    /// <param name="catalogs">
    /// The active catalogs. With one catalog the compiled surface names it. With several, each component and function
    /// call names the catalog that defines it, and a name that several catalogs define needs a <c>catalogId</c> argument.
    /// </param>
    /// <param name="messages">The validated messages in the order of the statements that produced them on success.</param>
    /// <param name="surfaceId">The surface used by statements that name none.</param>
    /// <returns>A <c>ParseError</c> or <c>ValidationError</c> diagnostic on failure.</returns>
    public static A2uiDiagnostic? Compile(string raw, CatalogRegistry catalogs, out IReadOnlyList<WireMessage> messages,
        string surfaceId = DefaultSurfaceId)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(surfaceId);
        messages = Array.Empty<WireMessage>();
        try
        {
            var compiled = new ExpressCompiler(ActiveCatalogs(catalogs)).Compile(raw, surfaceId);
            var decoded = new List<WireMessage>(compiled.Count);
            foreach (var message in compiled)
            {
                if (!WireMessage.TryDecode(PlainJson.ToUtf8(message), WireDirection.AgentToRenderer, out var wire, out var error))
                    return error! with { Code = "ValidationError" };
                decoded.Add(wire!);
            }

            if (DirectJson.Validate(decoded, catalogs) is { } invalid)
                return invalid;
            messages = decoded.AsReadOnly();
            return null;
        }
        catch (ExpressException error)
        {
            return new A2uiDiagnostic(error.Code, string.Empty, error.Message);
        }
    }

    /// <summary>Writes messages as Express text, so that a prompt example authored as messages can be shown to a model.</summary>
    /// <param name="messages">The messages to write, in order. Messages for one surface are merged under one <c>surface(...)</c> line.</param>
    /// <param name="catalogs">The catalogs that declare the components and functions the messages use.</param>
    /// <param name="text">The Express text on success. It compiles back to the messages, or to their merged form.</param>
    /// <returns>A <c>ValidationError</c> diagnostic when a message uses a name the catalogs do not declare or the notation cannot express.</returns>
    public static A2uiDiagnostic? Decompile(IEnumerable<WireMessage> messages, CatalogRegistry catalogs, out string text)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(catalogs);
        text = string.Empty;
        try
        {
            var trees = messages.Select(message => (PlainMap)PlainJson.FromElement(message.Envelope)!);
            text = new ExpressDecompiler(ActiveCatalogs(catalogs)).Decompile(trees);
            return null;
        }
        catch (ExpressException error)
        {
            return new A2uiDiagnostic(error.Code, string.Empty, error.Message);
        }
    }

    /// <summary>Compiles one complete model response and prepares its state mutations atomically.</summary>
    /// <param name="response">The complete model response.</param>
    /// <param name="state">The peer's mirror across turns.</param>
    /// <param name="parts">Ordered output on success, or the decoded prefix for diagnostics on failure.</param>
    /// <param name="turn">A validated transaction on success. Commit it before emitting UI messages.</param>
    /// <param name="wrapped">Whether the model used sentinel tags around blocks. When false the whole response is one block.</param>
    /// <param name="surfaceId">The surface used by statements that name none.</param>
    /// <param name="cancellationToken">Cancels processing before commit.</param>
    /// <returns>
    /// A parse or validation diagnostic, or null on success. A block that is missing its closing tag is a parse
    /// error: only complete blocks are compiled.
    /// </returns>
    public static A2uiDiagnostic? ParseResponse(string response, RendererState state, out IReadOnlyList<ExpressResponsePart> parts,
        out RendererTurn? turn, bool wrapped = true, string surfaceId = DefaultSurfaceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        var decoded = new List<ExpressResponsePart>();
        var messages = new List<WireMessage>();
        parts = decoded.AsReadOnly();
        turn = null;
        foreach (var part in wrapped ? Unwrap(response) : [new ExpressPart(response, true)])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!part.IsPayload)
            {
                decoded.Add(new ExpressResponsePart(part.Content, null));
                continue;
            }

            if (!part.IsFinal)
                return new A2uiDiagnostic("ParseError", string.Empty, "Missing closing Express tag.");
            if (Compile(part.Content, state.Catalogs, out var block, surfaceId) is { } error)
                return error;
            messages.AddRange(block);
            decoded.Add(new ExpressResponsePart(null, block));
        }

        var issue = state.Prepare(messages, out turn, cancellationToken);
        return issue is null ? null : issue with { Code = "ValidationError" };
    }

    private static List<(string Id, JsonElement Definition)> ActiveCatalogs(CatalogRegistry catalogs)
    {
        var active = new List<(string, JsonElement)>();
        foreach (var id in catalogs.CatalogIds)
            if (catalogs.TryGetDefinition(id, out var definition))
                active.Add((id, definition));
        if (active.Count == 0)
            throw ExpressException.Validation("At least one catalog must be registered.");
        return active;
    }
}
