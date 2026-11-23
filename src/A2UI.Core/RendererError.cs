using System.Text.Json;

namespace A2UI.Core;

/// <summary>A renderer-side failure that is reported to the agent with the wire <c>error</c> message.</summary>
/// <remarks>
/// <see cref="Category"/> names the kind of failure for local handling and logs. <see cref="Code"/> is the code that
/// the protocol puts on the wire. Create an error from a rejection with <see cref="FromDiagnostic"/>,
/// <see cref="FromRejectedMessage"/> or <see cref="FromRejectedEnvelope"/>, and send <see cref="ToWireMessage"/>.
/// </remarks>
/// <param name="Category">The failure kind: <c>ValidationError</c>, <c>IntegrityError</c>, <c>CatalogError</c>, <c>RecursionError</c>, <c>DataError</c>, <c>ExpressionError</c> or <c>ActionError</c>.</param>
/// <param name="Code">The wire error code, such as <c>VALIDATION_FAILED</c> or <c>INTEGRITY_ERROR</c>.</param>
/// <param name="Message">A short description of the failure.</param>
public sealed record RendererError(string Category, string Code, string Message)
{
    private static readonly string[] EnvelopeKinds =
        ["createSurface", "updateComponents", "updateDataModel", "deleteSurface", "callRendererFunction", "agentFunctionResponse",
         "action", "callAgentFunction", "rendererFunctionResponse"];

    /// <summary>The surface where the failure occurred, or null when the failure belongs to a function call.</summary>
    public string? SurfaceId { get; init; }

    /// <summary>The function call that failed, or null when the failure belongs to a surface.</summary>
    public string? FunctionCallId { get; init; }

    /// <summary>The pointer to the invalid field, relative to the operation's payload. Only validation failures carry one.</summary>
    public string? Path { get; init; }

    /// <summary>Maps a diagnostic to the error that the protocol reports for it.</summary>
    /// <param name="diagnostic">The failure from decoding or applying a message, or from evaluating a function.</param>
    /// <param name="surfaceId">The surface the failure belongs to, if known.</param>
    /// <param name="functionCallId">The function call the failure belongs to, if known. A call identifier replaces the surface in the report.</param>
    /// <returns>The error to report.</returns>
    public static RendererError FromDiagnostic(A2uiDiagnostic diagnostic, string? surfaceId = null, string? functionCallId = null)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var (kind, path) = SplitPath(diagnostic.Path);
        var rpc = kind == "callRendererFunction";
        var (category, code) = rpc
            ? ("ValidationError", "INVALID_FUNCTION_CALL")
            : diagnostic.Code switch
            {
                "unknownCatalog" => ("CatalogError", "CATALOG_NOT_FOUND"),
                "CatalogError" => ("CatalogError", "CATALOG_ERROR"),
                "duplicateSurface" or "unknownSurface" or "duplicateComponent" or "danglingReference" or "missingRoot" or
                    "orphanComponent" => ("IntegrityError", "INTEGRITY_ERROR"),
                "circularReference" or "graphDepth" => ("RecursionError", "RECURSION_LIMIT_EXCEEDED"),
                "invalidDataPath" or "forbiddenPathKey" => ("DataError", "DATA_ERROR"),
                "unallowedParent" => ("ValidationError", "UNALLOWED_PARENT"),
                "unallowedChild" => ("ValidationError", "UNALLOWED_CHILD"),
                "ExpressionError" => ("ExpressionError", "EXPRESSION_ERROR"),
                "invalidAction" => ("ActionError", "INVALID_ACTION"),
                _ => ("ValidationError", "VALIDATION_FAILED")
            };
        var validation = code is "VALIDATION_FAILED" or "UNALLOWED_PARENT" or "UNALLOWED_CHILD";
        return new RendererError(category, code, validation || rpc ? NameField(diagnostic.Message, path) : diagnostic.Message)
        {
            SurfaceId = rpc ? null : surfaceId,
            FunctionCallId = functionCallId,
            Path = validation ? path : null
        };
    }

    /// <summary>Maps the rejection of a decoded message and reads the correlation identifiers from it.</summary>
    /// <param name="message">The message that was rejected, such as by <see cref="SurfaceSession.Apply"/>.</param>
    /// <param name="diagnostic">The rejection.</param>
    /// <returns>The error to report.</returns>
    public static RendererError FromRejectedMessage(WireMessage message, A2uiDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(message);
        return FromDiagnostic(diagnostic, ReadText(message.Payload, "surfaceId"), ReadText(message.Payload, "functionCallId"));
    }

    /// <summary>Maps the rejection of an envelope that could not be decoded and reads the correlation identifiers from its bytes.</summary>
    /// <param name="utf8">The envelope that <see cref="WireMessage.TryDecode"/> rejected.</param>
    /// <param name="diagnostic">The rejection.</param>
    /// <returns>The error to report. Identifiers are absent when the envelope is not readable.</returns>
    public static RendererError FromRejectedEnvelope(ReadOnlyMemory<byte> utf8, A2uiDiagnostic diagnostic)
    {
        string? surfaceId = null, functionCallId = null;
        if (WireJson.TryRead(utf8, out var envelope, out _) && WireJson.IsObject(envelope))
        {
            foreach (var member in envelope.EnumerateObject())
            {
                if (member.NameEquals("version") || member.Value.ValueKind != JsonValueKind.Object)
                    continue;
                surfaceId = ReadText(member.Value, "surfaceId");
                functionCallId = ReadText(member.Value, "functionCallId");
                break;
            }
        }
        return FromDiagnostic(diagnostic, surfaceId, functionCallId);
    }

    /// <summary>Builds the wire <c>error</c> message for this error.</summary>
    /// <returns>A validated renderer-to-agent message.</returns>
    /// <remarks>
    /// Validation failures always carry <c>surfaceId</c> and <c>path</c>. Other errors carry the function call identifier
    /// when there is one and otherwise the surface identifier, which is empty when the surface is unknown.
    /// </remarks>
    public WireMessage ToWireMessage()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("version", WireMessage.Version);
            writer.WriteStartObject("error");
            writer.WriteString("code", Code);
            if (Code is "VALIDATION_FAILED" or "UNALLOWED_PARENT" or "UNALLOWED_CHILD")
            {
                writer.WriteString("surfaceId", SurfaceId ?? "");
                writer.WriteString("path", Path ?? "");
            }
            else if (FunctionCallId is not null)
            {
                writer.WriteString("functionCallId", FunctionCallId);
            }
            else
            {
                writer.WriteString("surfaceId", SurfaceId ?? "");
            }
            writer.WriteString("message", Message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        if (!WireMessage.TryDecode(stream.ToArray(), WireDirection.RendererToAgent, out var message, out var problem))
            throw new InvalidOperationException($"The error does not form a valid message: {problem}");
        return message!;
    }

    // Diagnostics from decoding and applying a message start with the operation name. The wire error path does not.
    private static (string? Kind, string Path) SplitPath(string path)
    {
        foreach (var kind in EnvelopeKinds)
        {
            var prefix = "/" + kind;
            if (path == prefix)
                return (kind, "");
            if (path.StartsWith(prefix + "/", StringComparison.Ordinal))
                return (kind, path[prefix.Length..]);
        }
        return (null, path);
    }

    // Schema messages describe the rule. The field name tells the agent which member to fix.
    private static string NameField(string message, string path)
    {
        var field = path[(path.LastIndexOf('/') + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        return field.Length == 0 || message.Contains(field, StringComparison.Ordinal)
            ? message
            : $"{message.TrimEnd('.')} (field '{field}').";
    }

    private static string? ReadText(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
