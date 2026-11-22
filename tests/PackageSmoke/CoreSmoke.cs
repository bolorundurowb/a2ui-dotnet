using System.Buffers;
using System.Text;
using System.Text.Json;
using A2UI.Core;

internal static class CoreSmoke
{
    internal static void Run()
    {
        var registry = new CatalogRegistry();
        var custom = """
            {
              "catalogId":"test:cards","protocolVersion":"1.0",
              "components":{"Label":{"type":"object","properties":{
                "component":{"const":"Label"},"text":{"$ref":"common_types.json#/$defs/DynamicString"}
              },"required":["component","text"]}}
            }
            """;
        Require(registry.TryRegister(Encoding.UTF8.GetBytes(custom), out var registration), registration);
        var badCatalog = custom.Replace("test:cards", "test:broken", StringComparison.Ordinal)
            .Replace("common_types.json#/$defs/DynamicString", "https://example.invalid/schema", StringComparison.Ordinal);
        Require(!registry.TryRegister(Encoding.UTF8.GetBytes(badCatalog), out registration) &&
            registration?.Code == "unresolvedReference", registration);

        Require(registry.TryDerive(CatalogRegistry.BasicCatalogId, "test:text", ["Text"], [], null, out var derived), derived);
        Require(registry.TryGetDefinition("test:text", out var textOnly) &&
            textOnly.GetProperty("components").EnumerateObject().Count() == 1);
        Require(registry.Remove("test:text") && !registry.TryGetDefinition("test:text", out _));

        using var session = new SurfaceSession(registry);
        var create = Decode($$$"""
            {"version":"1.0","createSurface":{
              "surfaceId":"form","catalogId":"{{{CatalogRegistry.BasicCatalogId}}}","sendDataModel":true,
              "dataModel":{"name":"Ada","number":9007199254740993},
              "components":[{"id":"root","component":"Text","text":"Hello"},
                            {"id":"label","component":"Label","@catalogId":"test:cards",
                             "text":{"@call":"formatString","args":{"value":"Hello"} } }]}}
            """);
        var notifications = 0;
        session.Changed += change =>
        {
            notifications++;
            Require(session.TryGetSurface(change.SurfaceId, out _) == (change.Snapshot is not null));
            Require(session.Apply(create)?.Code == "reentrantUpdate");
        };
        Require(session.Apply(create) is null);
        Require(session.Apply(create)?.Code == "duplicateSurface");
        Require(session.TryGetSurface("form", out var snapshot) && snapshot!.RootComponentId == "root");
        Require(snapshot!.DataModel.GetProperty("number").GetRawText() == "9007199254740993");
        var invalid = Decode("""
            {"version":"1.0","updateComponents":{"surfaceId":"form","components":[
                {"id":"root","component":"Text","text":"Changed"},
                {"id":"bad","component":"Label","@catalogId":"test:cards","text":7}]}}
            """);
        var issue = session.Apply(invalid);
        Require(issue?.Path == "/updateComponents/components/1/text", issue);
        Require(session.TryGetSurface("form", out var unchanged) && ReferenceEquals(snapshot, unchanged));
        Require(notifications == 1);

        var update = Decode("""{"version":"1.0","updateDataModel":{"surfaceId":"form","value":null}}""");
        Require(update.Payload.GetProperty("value").ValueKind == JsonValueKind.Null);
        Require(session.Apply(update) is null);
        Require(session.TryGetSurface("form", out var reset) && reset!.DataModel.GetRawText() == "{}");
        var delete = Decode("""{"version":"1.0","deleteSurface":{"surfaceId":"form"}}""");
        Require(session.Apply(delete) is null && !session.TryGetSurface("form", out _));
        Require(session.Apply(delete) is null && notifications == 3);
        Require(session.Apply(create) is null && notifications == 4);
        CheckDataWrites(session);

        Decode("""{"version":"1.0","callRendererFunction":{"functionCallId":"c","callFunction":{"@call":"now","catalogId":"test:cards"}}}""");
        Decode("""{"version":"1.0","agentFunctionResponse":{"functionCallId":"c","value":null}}""");
        Decode("""{"version":"1.0","rendererFunctionResponse":{"functionCallId":"c","value":null}}""", WireDirection.RendererToAgent);
        Decode("""{"version":"1.0","callAgentFunction":{"surfaceId":"form","functionCallId":"c","callFunction":{"@call":"now"}}}""", WireDirection.RendererToAgent);
        Decode("""{"version":"1.0","action":{"name":"submit","surfaceId":"form","sourceComponentId":"root","timestamp":"2026-11-22T10:00:00Z","context":{}}}""",
            WireDirection.RendererToAgent);
        Decode("""{"version":"1.0","error":{"code":"OTHER","message":"Failed","surfaceId":"form","vendor":true}}""",
            WireDirection.RendererToAgent);

        Require(WireMetadata.TryDecode(Encoding.UTF8.GetBytes("""{"1.0":{"acceptsInlineCatalogs":false},"future":{}}"""),
            WireMetadataKind.AgentCapabilities, out var metadata, out var metadataError) &&
            metadata!.Value.TryGetProperty("future", out _), metadataError);
        Require(WireMetadata.TryDecode(Encoding.UTF8.GetBytes("""{"1.0":{"supportedCatalogIds":[]}}"""),
            WireMetadataKind.RendererCapabilities, out _, out metadataError), metadataError);
        Require(WireMetadata.TryDecode(Encoding.UTF8.GetBytes("""{"version":"1.0","surfaces":{"form":{}}}"""),
            WireMetadataKind.RendererDataModel, out _, out metadataError), metadataError);
        Require(!WireMessage.TryDecode(Encoding.UTF8.GetBytes("""
            {"version":"1.0","createSurface":{"surfaceId":"s","dataModel":{"a/b~":1,"a/b~":2}}}
            """), WireDirection.AgentToRenderer, out _, out var duplicate) &&
            duplicate?.Path == "/createSurface/dataModel/a~1b~0", duplicate);

        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
            create.WriteTo(writer);
        Require(WireMessage.TryDecode(output.WrittenMemory, WireDirection.AgentToRenderer, out var copy, out var roundTrip) &&
            copy!.Envelope.GetProperty("createSurface").GetProperty("dataModel").GetProperty("number").GetRawText() ==
            "9007199254740993", roundTrip);
    }

    private static void CheckDataWrites(SurfaceSession session)
    {
        var changes = new List<string?>();
        session.Changed += change => changes.Add(change.DataPath);
        using (var edit = JsonDocument.Parse("""{"local":true,"value":null}"""))
            Require(session.WriteData("form", "/edit", edit.RootElement) is null);

        Require(session.TryGetSurface("form", out var edited) &&
            edited!.TryGetData("/edit/value", out var value) && value.ValueKind == JsonValueKind.Null);
        Require(!edited!.TryGetData("/edit/missing", out _));
        Require(session.Apply(Decode("""
            {"version":"1.0","updateDataModel":{"surfaceId":"form","path":"/edit/local","value":false}}
            """)) is null);
        Require(session.TryGetSurface("form", out var current) &&
            current!.TryGetData("/edit/local", out var currentValue) && !currentValue.GetBoolean());
        Require(edited.TryGetData("/edit/local", out var priorValue) && priorValue.GetBoolean());
        Require(changes.SequenceEqual(new[] { "/edit", "/edit/local" }));
        Require(!WireMessage.TryDecode("""
            {"version":"1.0","updateDataModel":{"surfaceId":"form","path":"/edit/~2","value":0}}
            """u8.ToArray(), WireDirection.AgentToRenderer, out _, out var error) &&
            error?.Path == "/updateDataModel/path", error);
    }

    private static WireMessage Decode(string json, WireDirection direction = WireDirection.AgentToRenderer)
    {
        var buffer = Encoding.UTF8.GetBytes(json);
        Require(WireMessage.TryDecode(buffer, direction, out var message, out var error), error);
        Array.Clear(buffer);
        return message!;
    }

    private static void Require(bool condition, A2uiDiagnostic? diagnostic = null)
    {
        if (!condition)
            throw new InvalidOperationException(diagnostic?.ToString() ?? "Core smoke assertion failed.");
    }
}
