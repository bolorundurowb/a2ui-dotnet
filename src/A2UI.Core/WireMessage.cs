using System.Text.Json;

namespace A2UI.Core;

/// <summary>Identifies the sender and recipient of an A2UI envelope.</summary>
public enum WireDirection
{
    /// <summary>A message sent by the agent to the renderer.</summary>
    AgentToRenderer,
    /// <summary>A message sent by the renderer to the agent.</summary>
    RendererToAgent
}

/// <summary>Identifies the schema for an A2UI metadata value.</summary>
public enum WireMetadataKind
{
    /// <summary>Capabilities advertised by an agent.</summary>
    AgentCapabilities,
    /// <summary>Capabilities advertised by a renderer.</summary>
    RendererCapabilities,
    /// <summary>Data model snapshots synchronized by a renderer.</summary>
    RendererDataModel
}

/// <summary>An owned A2UI 1.0 envelope. Optional members retain their original JSON representation.</summary>
public sealed class WireMessage
{
    private WireMessage(WireDirection direction, string kind, JsonElement envelope)
    {
        Direction = direction;
        Kind = kind;
        Envelope = envelope;
    }

    /// <summary>The supported A2UI wire protocol version.</summary>
    public const string Version = "1.0";
    /// <summary>The direction associated with this envelope.</summary>
    public WireDirection Direction { get; }
    /// <summary>The wire property naming the operation represented by this envelope.</summary>
    public string Kind { get; }
    /// <summary>The complete, owned JSON envelope.</summary>
    public JsonElement Envelope { get; }
    /// <summary>The JSON value of the operation property.</summary>
    public JsonElement Payload => Envelope.GetProperty(Kind);

    /// <summary>Reads and validates one UTF-8 wire envelope without retaining the input buffer.</summary>
    /// <param name="utf8">The complete JSON envelope.</param>
    /// <param name="direction">The expected direction of the message.</param>
    /// <param name="message">The decoded message on success.</param>
    /// <param name="diagnostic">The validation failure, if any.</param>
    /// <returns>Whether the envelope was decoded. Catalog-specific checks require surface context.</returns>
    public static bool TryDecode(ReadOnlyMemory<byte> utf8, WireDirection direction,
        out WireMessage? message, out A2uiDiagnostic? diagnostic)
    {
        message = null;
        if (!WireJson.TryRead(utf8, out var envelope, out diagnostic))
            return false;
        if (!WireJson.IsObject(envelope))
            return Reject("invalidEnvelope", "", "Expected a JSON object.", out diagnostic);
        if (!envelope.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
            version.GetString() != Version)
            return Reject("unsupportedVersion", "/version", "Expected A2UI version 1.0.", out diagnostic);

        var kinds = direction switch
        {
            WireDirection.AgentToRenderer => AgentKinds,
            WireDirection.RendererToAgent => RendererKinds,
            _ => throw new ArgumentOutOfRangeException(nameof(direction))
        };
        string? kind = null;
        foreach (var property in envelope.EnumerateObject())
        {
            if (property.NameEquals("version"))
                continue;
            if (Array.IndexOf(kinds, property.Name) >= 0)
            {
                if (kind is not null)
                    return Reject("invalidEnvelope", "", "Expected exactly one message kind.", out diagnostic);
                kind = property.Name;
            }
            else if (direction == WireDirection.AgentToRenderer)
                return Reject("invalidEnvelope", WireJson.Path("", property.Name), "Unknown envelope member.", out diagnostic);
        }

        if (kind is null)
            return Reject("invalidEnvelope", "", "Expected exactly one message kind.", out diagnostic);

        diagnostic = ProtocolSchemas.ValidateMessage(envelope, kind, direction);
        if (diagnostic is not null)
            return false;
        if (kind == "updateDataModel" && envelope.GetProperty(kind).TryGetProperty("path", out var path) &&
            !DataPointer.TryParse(path.GetString()!, out _))
            return Reject("invalidPath", "/updateDataModel/path", "Invalid path syntax or depth.", out diagnostic);
        message = new(direction, kind, envelope);
        return true;
    }

    /// <summary>Writes the envelope as one JSON value.</summary>
    /// <param name="writer">The destination JSON writer.</param>
    public void WriteTo(Utf8JsonWriter writer) => Envelope.WriteTo(writer);

    private static bool Reject(string code, string path, string message, out A2uiDiagnostic diagnostic)
    {
        diagnostic = new(code, path, message);
        return false;
    }

    private static readonly string[] AgentKinds =
        ["createSurface", "updateComponents", "updateDataModel", "deleteSurface", "callRendererFunction", "agentFunctionResponse"];
    private static readonly string[] RendererKinds =
        ["action", "callAgentFunction", "rendererFunctionResponse", "error"];
}

/// <summary>An owned A2UI metadata value validated against its versioned schema.</summary>
public sealed class WireMetadata
{
    private WireMetadata(WireMetadataKind kind, JsonElement value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>The schema used to validate the metadata.</summary>
    public WireMetadataKind Kind { get; }
    /// <summary>The complete, owned metadata value, including extensions.</summary>
    public JsonElement Value { get; }

    /// <summary>Reads and validates UTF-8 metadata without retaining the input buffer.</summary>
    /// <param name="utf8">The complete JSON metadata value.</param>
    /// <param name="kind">The metadata schema to apply.</param>
    /// <param name="metadata">The decoded metadata on success.</param>
    /// <param name="diagnostic">The validation failure, if any.</param>
    /// <returns>Whether the metadata was decoded.</returns>
    public static bool TryDecode(ReadOnlyMemory<byte> utf8, WireMetadataKind kind,
        out WireMetadata? metadata, out A2uiDiagnostic? diagnostic)
    {
        metadata = null;
        if (!WireJson.TryRead(utf8, out var value, out diagnostic))
            return false;
        diagnostic = ProtocolSchemas.ValidateMetadata(value, kind);
        if (diagnostic is not null)
            return false;
        metadata = new(kind, value);
        return true;
    }

    /// <summary>Writes the metadata as one JSON value.</summary>
    /// <param name="writer">The destination JSON writer.</param>
    public void WriteTo(Utf8JsonWriter writer) => Value.WriteTo(writer);
}
