using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using A2UI.Core;

internal static class CoreSmoke
{
    internal static void Run()
    {
        var registry = new CatalogRegistry();
        CheckOccurrences(registry);
        CheckSessionDisposal(registry);
        CheckCatalogValidation();
        CheckCatalogDerivation();
        CheckValidationSchema();
        CheckCandidateGraph();
        CheckProgressiveLeafComposition();
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

        var invalidBinding = Decode("""
            {"version":"1.0","updateComponents":{"surfaceId":"form","components":[
              {"id":"root","component":"Text","text":{"@path":"profile/~2"}}]}}
            """);
        Require(session.Apply(invalidBinding) is not null);
        Require(session.TryGetSurface("form", out unchanged) && ReferenceEquals(snapshot, unchanged));
        Require(notifications == 1);

        var mixedUpdate = Decode("""
            {"version":"1.0","updateComponents":{"surfaceId":"form","components":[
              {"id":"root","component":"Text","text":{"@path":"profile/name"}},
              {"id":"label","component":"Label","@catalogId":"test:cards","text":{"@path":"/a~1b/~0c"}}]}}
            """);
        Require(session.Apply(mixedUpdate) is null);
        Require(session.TryGetSurface("form", out var mixed) && mixed!.Components.Count == 2);
        Require(notifications == 2);

        var update = Decode("""{"version":"1.0","updateDataModel":{"surfaceId":"form","value":null}}""");
        Require(update.Payload.GetProperty("value").ValueKind == JsonValueKind.Null);
        Require(session.Apply(update) is null);
        Require(session.TryGetSurface("form", out var reset) && reset!.DataModel.GetRawText() == "{}");
        var delete = Decode("""{"version":"1.0","deleteSurface":{"surfaceId":"form"}}""");
        Require(session.Apply(delete) is null && !session.TryGetSurface("form", out _));
        Require(session.Apply(delete) is null && notifications == 4);
        Require(session.Apply(create) is null && notifications == 5);
        CheckDataWrites(session);
        CheckDataModel(registry);
        CheckExpressionScope();
        CheckNodeResolver(registry);
        CheckNestedTemplateIdentity(registry);
        CheckFunctions(registry);
        CheckOutbound(registry);
        CheckRpc(registry);
        CheckMountedAgentCalls();
        CheckMountedDisposalOrder();
        CheckMountedConcurrentReplies();

        session.TryGetSurface("form", out var beforeCancellation);
        var cancelled = new CancellationToken(canceled: true);
        try
        {
            session.Apply(delete, cancelled);
            throw new InvalidOperationException("Expected a cancelled surface operation.");
        }
        catch (OperationCanceledException error) when (error.CancellationToken == cancelled)
        {
            Require(session.TryGetSurface("form", out var afterCancellation) &&
                ReferenceEquals(beforeCancellation, afterCancellation) && notifications == 7);
        }

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
        CheckWireFidelity();
    }

    // Pins how the codec treats number text, absent members, explicit null and input ownership. Decode clears the
    // input buffer, so every read below also proves that the message does not borrow from its input.
    private static void CheckCatalogValidation()
    {
        var registry = new CatalogRegistry();
        const string definition = """
            {"catalogId":"custom","protocolVersion":"1.0",
             "components":{"Label_ℓ":{"type":"object","properties":{"property_ℓ":{"type":"string"}}}},
             "functions":{"notify":{"type":"object","returnType":"void","requiresUserActivation":true,
               "properties":{"@call":{"const":"notify"},"args":{"type":"object","properties":{"arg_ℓ":{"type":"string"}}}},
               "required":["@call"]}}}
            """;
        Require(!registry.TryRegister(Encoding.UTF8.GetBytes(definition.Replace("arg_ℓ", "bad-arg", StringComparison.Ordinal)),
            out var error) && error?.Code == "invalidCatalogItem" &&
            error.Path == "/functions/notify/properties/args/properties/bad-arg", error);
        Require(registry.TryRegister(Encoding.UTF8.GetBytes(definition), out error), error);

        using var session = new SurfaceSession(registry, GraphValidationOptions.Strict);
        Require(session.Apply(Decode($$$"""
            {"version":"1.0","createSurface":{"surfaceId":"mixed","catalogId":"{{{CatalogRegistry.BasicCatalogId}}}",
             "components":[{"id":"root","component":"Column","children":["custom"]},
               {"id":"custom","component":"Label_ℓ","@catalogId":"custom","property_ℓ":"value"}]}}
            """)) is null);
        var rejection = session.Apply(Decode("""
            {"version":"1.0","updateComponents":{"surfaceId":"mixed","components":[
              {"id":"custom","component":"Label_ℓ","@catalogId":"missing","property_ℓ":"value"}]}}
            """));
        Require(rejection?.Code == "unknownCatalog", rejection);
    }

    private static void CheckCatalogDerivation()
    {
        var registry = new CatalogRegistry();
        using var label = JsonDocument.Parse("""
            {"type":"object","properties":{"component":{"const":"Label"},"text":{"type":"string"}},"required":["component"]}
            """);
        using var text = JsonDocument.Parse("""
            {"type":"object","properties":{"component":{"const":"Text"},"text":{"type":"string"}},"required":["component"]}
            """);

        // A defined entry adds Label and replaces Text, while Button keeps the Basic Catalog definition.
        Require(registry.TryDerive(CatalogRegistry.BasicCatalogId, "test:derived", new CatalogDerivation
        {
            Components = [CatalogEntry.Keep("Button"), CatalogEntry.Define("Label", label.RootElement), CatalogEntry.Define("Text", text.RootElement)],
            Functions = [CatalogEntry.Keep("openUrl")]
        }, out var diagnostic), diagnostic);
        Require(registry.TryGetDefinition(CatalogRegistry.BasicCatalogId, out var basic));
        Require(registry.TryGetDefinition("test:derived", out var derived));
        Require(derived.GetProperty("components").EnumerateObject().Select(entry => entry.Name)
            .OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(["Button", "Label", "Text"]));
        Require(SameJson(derived.GetProperty("components").GetProperty("Button"), basic.GetProperty("components").GetProperty("Button")));
        Require(SameJson(derived.GetProperty("components").GetProperty("Text"), text.RootElement));
        Require(derived.GetProperty("functions").EnumerateObject().Single().Name == "openUrl");
        Require(!derived.TryGetProperty("instructions", out _) && !derived.TryGetProperty("$id", out _) &&
            !derived.TryGetProperty("title", out _) && !derived.TryGetProperty("description", out _));
        Require(basic.TryGetProperty("instructions", out _), diagnostic);

        // The unions in $defs follow the entries, so the new component validates and a dropped one does not.
        var union = derived.GetProperty("$defs").GetProperty("anyComponent").GetProperty("oneOf").EnumerateArray()
            .Select(branch => branch.GetProperty("$ref").GetString()).Order(StringComparer.Ordinal).ToArray();
        Require(union.SequenceEqual(["#/components/Button", "#/components/Label", "#/components/Text"]));
        Require(registry.ValidateComponent(JsonDocument.Parse("""{"id":"a","component":"Label","text":"Hi"}""").RootElement, "test:derived") is null);
        Require(registry.ValidateComponent(JsonDocument.Parse("""{"id":"a","component":"Image","url":"x"}""").RootElement, "test:derived")?.Code
            == "unknownCatalogItem");

        // Instructions are the caller's, and a list left out keeps every entry of that kind.
        Require(registry.TryDerive("test:derived", "test:copy", new CatalogDerivation { Instructions = "Use test:copy." }, out diagnostic), diagnostic);
        Require(registry.TryGetDefinition("test:copy", out var copy) && copy.GetProperty("instructions").GetString() == "Use test:copy." &&
            copy.GetProperty("components").EnumerateObject().Count() == 3);

        // A failed derivation reports the unknown name and registers nothing.
        Require(!registry.TryDerive("test:derived", "test:failed", new CatalogDerivation { Components = [CatalogEntry.Keep("Slider")] },
            out diagnostic) && diagnostic?.Code == "unknownCatalogItem" && diagnostic.Message.Contains("component 'Slider'", StringComparison.Ordinal) &&
            diagnostic.Path == "/components/Slider", diagnostic);
        Require(!registry.TryDerive("test:derived", "test:failed", ["Label"], ["capitalize"], null, out diagnostic) &&
            diagnostic?.Message.Contains("function 'capitalize'", StringComparison.Ordinal) == true, diagnostic);
        Require(!registry.TryDerive("test:derived", "test:failed", new CatalogDerivation { Components = [CatalogEntry.Define("Broken", JsonDocument.Parse("[]").RootElement)] },
            out diagnostic) && diagnostic?.Code == "invalidCatalogItem", diagnostic);
        Require(!registry.TryDerive("test:missing", "test:failed", new CatalogDerivation(), out diagnostic) &&
            diagnostic?.Code == "unknownCatalog", diagnostic);
        Require(!registry.TryDerive("test:derived", "test:copy", new CatalogDerivation(), out diagnostic) &&
            diagnostic?.Code == "duplicateCatalog", diagnostic);
        Require(!registry.CatalogIds.Contains("test:failed"));
    }

    private static void CheckValidationSchema()
    {
        var registry = new CatalogRegistry();
        Require(!registry.TryCreateValidationSchema("test:missing", out _));
        Require(registry.TryCreateValidationSchema(CatalogRegistry.BasicCatalogId, out var basic));
        var button = basic.GetProperty("components").GetProperty("Button");
        Require(button.GetProperty("required").EnumerateArray().Select(name => name.GetString()).Contains("id") &&
            !button.TryGetProperty("allOf", out _) && !button.GetProperty("properties").TryGetProperty("@catalogId", out _));
        Require(button.GetProperty("properties").GetProperty("id").GetProperty("$ref").GetString() == "#/$defs/ComponentId");
        Require(!basic.TryGetProperty("$id", out _) && !basic.TryGetProperty("title", out _) && basic.TryGetProperty("instructions", out _));
        RequireLocalReferences(basic, basic, "");

        // A local mixin is flattened, the component's own property wins, and a reference to a leaf def keeps its own description.
        const string definition = """
            {"catalogId":"test:mixins","protocolVersion":"1.0",
             "components":{"Gauge":{"type":"object","description":"A gauge.","allOf":[
                {"$ref":"common_types.json#/$defs/ComponentCommon"},
                {"$ref":"#/$defs/Shared"},
                {"type":"object","properties":{"component":{"const":"Gauge"},"level":{"type":"string"},
                  "tint":{"$ref":"#/$defs/Tint"}},"required":["component"]}],
               "unevaluatedProperties":false}},
             "$defs":{
               "Shared":{"type":"object","properties":{"level":{"type":"number"},"unit":{"type":"string"}},"required":["unit"]},
               "Tint":{"type":"string","description":"A colour."},
               "anyComponent":{"oneOf":[{"$ref":"#/components/Gauge"}],"discriminator":{"propertyName":"component"}},
               "anyFunction":{"not":{}}}}
            """;
        Require(registry.TryRegister(Encoding.UTF8.GetBytes(definition), out var diagnostic), diagnostic);
        Require(registry.TryCreateValidationSchema("test:mixins", out var custom));
        var gauge = custom.GetProperty("components").GetProperty("Gauge");
        var properties = gauge.GetProperty("properties");
        Require(gauge.GetProperty("description").GetString() == "A gauge." && gauge.GetProperty("unevaluatedProperties").ValueKind == JsonValueKind.False);
        Require(properties.GetProperty("level").GetProperty("type").GetString() == "string" &&
            properties.GetProperty("unit").GetProperty("type").GetString() == "string");
        Require(properties.GetProperty("tint").EnumerateObject().Select(member => member.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(["$ref"]));
        Require(properties.TryGetProperty("@accessibility", out _) && properties.TryGetProperty("@metadata", out _) &&
            !properties.TryGetProperty("@catalogId", out _));
        Require(gauge.GetProperty("required").EnumerateArray().Select(name => name.GetString()).Order(StringComparer.Ordinal)
            .SequenceEqual(["component", "id", "unit"]));
        var definitions = custom.GetProperty("$defs");
        Require(definitions.TryGetProperty("Tint", out _) && definitions.TryGetProperty("Extensions", out _) &&
            definitions.TryGetProperty("AccessibilityAttributes", out _) && !definitions.TryGetProperty("Surface", out _));
        RequireLocalReferences(custom, custom, "");
    }

    private static bool SameJson(JsonElement left, JsonElement right) =>
        System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(left.GetRawText()), System.Text.Json.Nodes.JsonNode.Parse(right.GetRawText()));

    // The schema is self-contained when every reference is a pointer into the document that resolves.
    private static void RequireLocalReferences(JsonElement value, JsonElement document, string path)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                RequireLocalReferences(item, document, path);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var member in value.EnumerateObject())
            {
                if (member.NameEquals("$ref") && member.Value.ValueKind == JsonValueKind.String)
                {
                    var reference = member.Value.GetString()!;
                    var target = document;
                    Require(reference.StartsWith("#/", StringComparison.Ordinal), new("externalReference", path, reference));
                    foreach (var segment in reference[2..].Split('/'))
                        Require(target.ValueKind == JsonValueKind.Object && target.TryGetProperty(segment, out target),
                            new("unresolvedReference", path, reference));
                }
                else if (member.Name is not ("default" or "const" or "enum" or "examples"))
                    RequireLocalReferences(member.Value, document, path + "/" + member.Name);
            }
        }
    }
    private static void CheckCandidateGraph()
    {
        var registry = new CatalogRegistry();
        const string definition = """
            {"catalogId":"test:rows","protocolVersion":"1.0","components":{"Label":
              {"type":"object","properties":{"text":{"$ref":"common_types.json#/$defs/DynamicString"}}}}}
            """;
        Require(registry.TryRegister(Encoding.UTF8.GetBytes(definition), out var registration), registration);

        using var session = new SurfaceSession(registry);
        var notifications = 0;
        session.Changed += _ => notifications++;
        Require(session.Apply(Decode($$$"""
            {"version":"1.0","createSurface":{"surfaceId":"graph","catalogId":"{{{CatalogRegistry.BasicCatalogId}}}",
             "components":[{"id":"label","component":"Label","@catalogId":"test:rows","text":{"@call":"@index"}}]}}
            """)) is null);
        Require(session.Apply(Decode("""
            {"version":"1.0","updateComponents":{"surfaceId":"graph","components":[
              {"id":"root","component":"List","children":{"path":"/rows","componentId":"label"}}]}}
            """)) is null);
        Require(session.TryGetSurface("graph", out var before));

        var rejection = session.Apply(Decode("""
            {"version":"1.0","updateComponents":{"surfaceId":"graph","components":[
              {"id":"root","component":"Column","children":["list","label"]},
              {"id":"list","component":"List","children":{"path":"/rows","componentId":"label"}}]}}
            """));
        Require(rejection?.Code == "indexOutsideCollection", rejection);
        Require(session.TryGetSurface("graph", out var after) && ReferenceEquals(before, after) &&
            !after!.Components.ContainsKey("list") && notifications == 2);
    }

    private static void CheckProgressiveLeafComposition()
    {
        var registry = new CatalogRegistry();
        const string definition = """
            {"catalogId":"test:composition","protocolVersion":"1.0","components":{
              "Container":{"type":"object","properties":{"child":{"$ref":"common_types.json#/$defs/Child"}},
                "allowedChildren":["Label"]},
              "Label":{"type":"object","allowedParents":["Container"]},
              "Other":{"type":"object"}}}
            """;
        Require(registry.TryRegister(Encoding.UTF8.GetBytes(definition), out var registration), registration);
        using var session = new SurfaceSession(registry);
        Require(session.Apply(Decode("""
            {"version":"1.0","createSurface":{"surfaceId":"leaf","catalogId":"test:composition",
              "components":[{"id":"root","component":"Container","child":"label"}]}}
            """)) is null);
        Require(session.TryGetSurface("leaf", out var before));

        using var fork = session.Fork();
        var rejection = fork.Apply(Decode("""
            {"version":"1.0","updateComponents":{"surfaceId":"leaf","components":[
              {"id":"label","component":"Other"}]}}
            """));
        Require(rejection?.Code == "unallowedChild", rejection);
        Require(fork.TryGetSurface("leaf", out var rejected) && ReferenceEquals(before, rejected));

        Require(fork.Apply(Decode("""
            {"version":"1.0","updateComponents":{"surfaceId":"leaf","components":[
              {"id":"label","component":"Label"}]}}
            """)) is null);
        Require(fork.TryGetSurface("leaf", out var after) && after!.Components.ContainsKey("label"));
        Require(session.TryGetSurface("leaf", out var original) && ReferenceEquals(before, original) &&
            !original!.Components.ContainsKey("label"));
    }

    private static void CheckSessionDisposal(CatalogRegistry registry)
    {
        using var session = new SurfaceSession(registry);
        var (snapshot, subscriber) = PopulateSession(session);
        session.Dispose();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Require(!snapshot.IsAlive && !subscriber.IsAlive);
        GC.KeepAlive(session);
    }

    // Keep the snapshot and delegate locals out of the stack frame that forces collection.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Snapshot, WeakReference Subscriber) PopulateSession(SurfaceSession session)
    {
        var notifications = 0;
        Action<SurfaceChange> subscriber = _ => notifications++;
        session.Changed += subscriber;
        Require(session.Apply(Decode("""
            {"version":"1.0","createSurface":{"surfaceId":"disposal"}}
            """)) is null);
        Require(session.TryGetSurface("disposal", out var snapshot) && notifications == 1);
        return (new WeakReference(snapshot), new WeakReference(subscriber));
    }

    private static void CheckWireFidelity()
    {
        string[] numbers = ["1.0", "1.00E+2", "-0.0", "123456789012345678901234567890.123456789", "1e400", "0.1"];
        var decoded = Decode("""{"version":"1.0","updateDataModel":{"surfaceId":"s","value":{"numbers":[""" +
            string.Join(",", numbers) + """]}}}""");
        Require(RawNumbers(decoded).SequenceEqual(numbers));
        Require(RawNumbers(RoundTrip(decoded)).SequenceEqual(numbers));

        // The protocol requires updateDataModel.value and uses an explicit null to delete, so absence is an error.
        var explicitNull = Decode("""{"version":"1.0","updateDataModel":{"surfaceId":"s","path":"/a","value":null}}""");
        foreach (var message in new[] { explicitNull, RoundTrip(explicitNull) })
            Require(message.Payload.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Null);
        Require(!WireMessage.TryDecode("""{"version":"1.0","updateDataModel":{"surfaceId":"s","path":"/a"}}"""u8.ToArray(),
            WireDirection.AgentToRenderer, out _, out var absent) && absent?.Path == "/updateDataModel/value", absent);

        var registry = new CatalogRegistry();
        using var session = new SurfaceSession(registry);
        Require(session.Apply(Decode($$$$"""
            {"version":"1.0","createSurface":{"surfaceId":"s","catalogId":"{{{{CatalogRegistry.BasicCatalogId}}}}","dataModel":{"stored":null}}}
            """)) is null);
        Require(session.TryGetSurface("s", out var surface) && surface is not null);
        Require(surface!.TryGetData("/stored", out var stored) && stored.ValueKind == JsonValueKind.Null);
        Require(!surface.TryGetData("/unresolved", out _));

        var unresolved = Decode("""{"version":"1.0","createSurface":{"surfaceId":"other","catalogId":"urn:missing"}}""");
        Require(session.Apply(unresolved)?.Code == "unknownCatalog");
    }

    private static IEnumerable<string> RawNumbers(WireMessage message) =>
        message.Payload.GetProperty("value").GetProperty("numbers").EnumerateArray().Select(number => number.GetRawText());

    private static WireMessage RoundTrip(WireMessage message)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
            message.WriteTo(writer);
        var buffer = output.WrittenMemory.ToArray();
        Require(WireMessage.TryDecode(buffer, message.Direction, out var copy, out var error), error);
        Array.Clear(buffer);
        return copy!;
    }

    private static void CheckExpressionScope()
    {
        using var data = JsonDocument.Parse("""{"label":"root","items":[{"label":"item"}]}""");
        var root = data.RootElement;
        var scope = root.GetProperty("items")[0];
        var calls = new List<(string? Catalog, string Name)>();
        var success = ExpressionTemplate.TryEvaluate(
            "${label}:${outer(value: inner(value: /label), index: @index())}:${missing}",
            root, scope, "test:formatting", Invoke, out var result, out var error);
        Require(success && result == "item:root:", error);
        Require(calls.SequenceEqual(new (string?, string)[]
        {
            ("test:formatting", "inner"),
            (null, "@index"),
            ("test:formatting", "outer")
        }));

        JsonElement Invoke(string? catalog, string name, IReadOnlyDictionary<string, JsonElement> arguments)
        {
            calls.Add((catalog, name));
            if (name == "@index")
                return scope;
            if (name == "outer")
                Require(arguments["index"].GetProperty("label").ValueEquals("item"));
            return arguments["value"];
        }
    }

    private static void CheckNodeResolver(CatalogRegistry registry)
    {
        using var session = new SurfaceSession(registry);
        using var resolver = new NodeResolver(session, registry, "nodes");
        var created = new List<string>();
        var changes = 0;
        resolver.NodeCreated += node => created.Add(node.Position);
        resolver.PropsChanged += _ => changes++;
        var create = Decode($$$"""
            {"version":"1.0","createSurface":{"surfaceId":"nodes","catalogId":"{{{CatalogRegistry.BasicCatalogId}}}",
              "dataModel":{"items":[{"name":"Ada"},{"name":"Lin"}]},
              "components":[{"id":"root","component":"Column","children":{"componentId":"name","path":"/items"}},
                {"id":"name","component":"Text","text":{"@path":"name"}}]}}
            """);
        Require(session.Apply(create) is null);
        Require(created.SequenceEqual(new[] { "root", "root/children/0", "root/children/1" }));

        var first = resolver.Nodes[1];
        using var edit = JsonDocument.Parse("\"Grace\"");
        Require(resolver.Write(first, "text", edit.RootElement) is null);
        Require(changes == 1 && ReferenceEquals(first, resolver.Nodes[1]));
        Require(resolver.Write(resolver.Nodes[0], "children", edit.RootElement)?.Code == "notWritable");

        Require(session.Apply(Decode("""{"version":"1.0","deleteSurface":{"surfaceId":"nodes"}}""")) is null);
        Require(resolver.Nodes.Count == 0);
    }

    private static void CheckNestedTemplateIdentity(CatalogRegistry registry)
    {
        using var session = new SurfaceSession(registry);
        using var resolver = new NodeResolver(session, registry, "nested");
        var create = Decode($$$"""
            {"version":"1.0","createSurface":{"surfaceId":"nested","catalogId":"{{{CatalogRegistry.BasicCatalogId}}}",
              "dataModel":{"groups":[{"items":[{"name":"Ada"},{"name":"Lin"}]}]},
              "components":[{"id":"root","component":"Column","children":{"componentId":"group","path":"/groups"}},
                {"id":"group","component":"Column","children":{"componentId":"item","path":"items"}},
                {"id":"item","component":"Row","children":["name","index"]},
                {"id":"name","component":"Text","text":{"@path":"name"}},
                {"id":"index","component":"Text","text":{"@call":"@index"}}]}}
            """);
        Require(session.Apply(create) is null);

        ResolvedNode Item(string position, string component) =>
            resolver.Nodes.Single(node => node.Position == position && node.ComponentId == component);
        const string list = "root/children/0/children/";
        var group = Item("root/children/0", "group");
        var ada = Item(list + "0/children/0", "name");
        var lin = Item(list + "1/children/0", "name");
        var groupChanges = 0;
        var calls = 0;
        resolver.PropsChanged += node =>
        {
            calls++;
            if (node == group)
                groupChanges++;
            Require(resolver.Nodes.Contains(ada) &&
                session.TryGetSurface("nested", out var snapshot) &&
                snapshot!.TryGetScopedData(ada.DataPath, "name", out var value) &&
                value.ValueEquals(ada.Props["text"].Value!.Value.GetString()));
            Require(session.WriteData("nested", "/illegal", Json("true"))?.Code == "reentrantUpdate");
        };

        const string items = "/groups/0/items";
        Require(session.WriteData("nested", items, Json("""[{"name":"Lee"},{"name":"Lin"},{"name":"Ada"}]""")) is null);
        Require(ReferenceEquals(lin, Item(list + "1/children/0", "name")));
        Require(ReferenceEquals(ada, Item(list + "2/children/0", "name")));
        Require(Item(list + "2/children/1", "index").Props["text"].Value?.GetInt32() == 2);
        Require(resolver.Write(ada, "text", Json("\"Grace\"")) is null);
        Require(ReferenceEquals(ada, Item(list + "2/children/0", "name")));
        Require(session.WriteData("nested", items, Json("""[{"name":"Lin"},{"name":"Grace"},{"name":"Lee"}]""")) is null);
        Require(ReferenceEquals(lin, Item(list + "0/children/0", "name")));
        Require(ReferenceEquals(ada, Item(list + "1/children/0", "name")));
        Require(session.WriteData("nested", items, Json("""[{"name":"Grace"},{"name":"Lee"}]""")) is null);
        Require(ReferenceEquals(ada, Item(list + "0/children/0", "name")));
        Require(resolver.Write(lin, "text", Json("\"stale\""))?.Code == "notWritable");
        Require(groupChanges == 3);

        Require(session.Apply(Decode("""{"version":"1.0","deleteSurface":{"surfaceId":"nested"}}""")) is null);
        var before = calls;
        Require(session.Apply(create) is null && resolver.Nodes.Count == 0 && calls == before);

        static JsonElement Json(string value)
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
    }

    private static void CheckFunctions(CatalogRegistry registry)
    {
        // The regular expression dialect: edges the pinned fixtures do not reach.
        Require(Valid("regex", """{"pattern":"^[a-]$","value":"-"}"""));
        Require(Valid("regex", """{"pattern":"^[😀]$","value":"😀"}"""));
        Require(!Valid("regex", """{"pattern":"^[😀]$","value":"a"}"""));
        Require(Valid("regex", """{"pattern":"^[^a]$","value":"😀"}"""));
        Require(!Valid("regex", """{"pattern":"^[^a]$","value":"a"}"""));
        Require(Valid("regex", """{"pattern":"^\\u{1F600}{2}$","value":"😀😀"}"""));
        Require(Valid("regex", """{"pattern":"^(?:a|)b$","value":"b"}"""));
        Require(Valid("regex", """{"pattern":"^(?:a?b)*$","value":"abb"}"""));
        Require(!Valid("regex", """{"pattern":"^.$","value":"\n"}"""));
        Require(!Valid("regex", """{"pattern":"^a$","value":"a\u2028"}"""));
        foreach (var rejected in new[] { """^[\d-z]$""", "a{2,1}", "a{", "a}", "]", "a**", "^*", "(?=a)*", """\-""", """\u{110000}""" })
        {
            var encoded = System.Text.Json.Nodes.JsonValue.Create(rejected)!.ToJsonString();
            Require(Fails("regex", $$"""{"pattern":{{encoded}},"value":"a"}""", "UNSUPPORTED_SYNTAX"));
        }
        Require(Fails("regex", """{"pattern":"(a+)(a+)","value":"a"}""", "ADJACENT_OVERLAP"));
        Require(Fails("regex", """{"pattern":"(?:a|ab)+","value":"a"}""", "OVERLAPPING_ALTERNATION"));

        // Dates, numbers and currencies.
        Require(Text("formatDate", """{"value":"2026-01-16T14:30:00Z","format":"MMM dd, yyyy"}""") == "Jan 16, 2026");
        Require(Text("formatDate", """{"value":"2026-01-16T14:30:00Z","format":"h:mm a"}""") == "2:30 PM");
        Require(Text("formatDate", """{"value":"2026-01-16T14:30:00Z","format":"EEEE, d MMMM"}""") == "Friday, 16 January");
        Require(Text("formatDate", """{"value":"2026-01-16T14:30:05.123Z","format":"yyyy 'at' HH:mm:ss.SSS ''yy"}""") ==
            "2026 at 14:30:05.123 '26");
        Require(Text("formatDate", """{"value":"2026-09-04T23:30:00-05:00","format":"ISO"}""") == "2026-09-05T04:30:00.000Z");
        Require(Text("formatDate", """{"value":"2026-09-04T23:30:00-05:00","format":"XXX"}""") == "-05:00");
        Require(Text("formatDate", """{"value":"2026-09-04T23:30:00Z","format":"xxx"}""") == "+00:00");
        Require(Text("formatDate", """{"value":"2026-09-04T24:00:00Z","format":"yyyy"}""") == "");
        Require(Text("formatDate", """{"value":1789000000,"format":"yyyy"}""") == "");
        Require(Fails("formatDate", """{"value":"2026-09-04","format":"QQ"}""", "Unsupported date pattern"));
        Require(Text("formatNumber", """{"value":1234.5678}""") == "1,234.568");
        Require(Text("formatNumber", """{"value":-1234.5,"decimals":1}""") == "-1,234.5");
        Require(Text("formatNumber", """{"value":0.5,"decimals":0}""") == "1");
        Require(Fails("formatNumber", """{"value":1,"decimals":21}""", "decimals"));
        Require(Text("formatCurrency", """{"value":1234.5,"currency":"EUR"}""") == "€1,234.50");
        Require(Text("formatCurrency", """{"value":1234,"currency":"JPY"}""") == "¥1,234");
        Require(Text("formatCurrency", """{"value":5,"currency":"chf"}""") == "CHF\u00A05.00");
        Require(Text("formatCurrency", """{"value":-5,"currency":"USD"}""") == "-$5.00");
        Require(Text("formatCurrency", """{"value":-0.001,"currency":"USD"}""") == "$0.00");
        Require(Fails("formatCurrency", """{"value":1,"currency":"US"}""", "currency"));
        Require(Text("pluralize", """{"value":1.5,"one":"one","other":"other"}""") == "other");
        Require(Text("pluralize", """{"value":1,"other":"other"}""") == "other");

        // Validators.
        Require(Valid("length", """{"value":"😀😀","max":2}"""));
        Require(!Valid("email", """{"value":"a@@b.cd"}"""));
        Require(!Valid("email", """{"value":"a @b.cd"}"""));
        Require(Valid("email", """{"value":"a@b.cd"}"""));
        Require(!Valid("numeric", """{"value":"1e999"}"""));

        // openUrl and @index through the evaluator.
        var opened = new List<Uri>();
        var evaluator = new FunctionEvaluator(registry, opened.Add);
        using var session = new SurfaceSession(registry);
        Require(session.Apply(Decode($$$"""
            {"version":"1.0","createSurface":{"surfaceId":"calls","catalogId":"{{{CatalogRegistry.BasicCatalogId}}}",
              "dataModel":{"items":[1,2]},"components":[{"id":"root","component":"Text","text":"x"}]}}
            """)) is null);
        Require(session.TryGetSurface("calls", out var surface));
        var open = Json("""{"@call":"openUrl","args":{"url":"https://example.com/a"}}""");
        Require(!evaluator.TryEvaluate(surface!, "/", open, userInitiated: false, out _, out var refusal) &&
            refusal?.Code == "ExpressionError" && refusal.Message.Contains("user activation", StringComparison.Ordinal), refusal);
        Require(opened.Count == 0);
        Require(evaluator.TryEvaluate(surface!, "/", open, userInitiated: true, out var none, out var failure) &&
            none.ValueKind == JsonValueKind.Null && opened.Single().Host == "example.com", failure);
        Require(evaluator.TryEvaluate(surface!, "/items/1",
            Json("""{"@call":"@index","args":{"offset":{"@path":"/items/0"}}}"""), false, out var index, out failure) &&
            index.GetInt32() == 2, failure);
        Require(!evaluator.TryEvaluate(surface!, "/items/1", Json("""{"@call":"@index","args":{"offset":1.5}}"""),
            false, out _, out failure) && failure?.Code == "ExpressionError", failure);
        Require(evaluator.TryEvaluate(surface!, "/", Json("""{"@call":"and","args":{"values":[{"@path":"/items"},{"@path":"/missing"}]}}"""),
            false, out var logical, out failure) && logical.ValueKind == JsonValueKind.False, failure);

        // Check rules: an unresolved condition does not block, and recovery is a single notification.
        using var checks = new SurfaceSession(registry);
        using var resolver = new NodeResolver(checks, registry, "form");
        var changes = 0;
        resolver.PropsChanged += _ => changes++;
        Require(checks.Apply(Decode($$$$"""
            {"version":"1.0","createSurface":{"surfaceId":"form","catalogId":"{{{{CatalogRegistry.BasicCatalogId}}}}",
              "dataModel":{"email":"nope"},"components":[{"id":"root","component":"TextField","label":"Email","value":{"@path":"/email"},
                "@checks":[{"condition":{"@path":"/unset"}},
                  {"condition":{"@call":"email","args":{"value":{"@path":"/email"}}},"message":"Bad email"}]}]}}
            """)) is null);
        var field = resolver.Nodes.Single();
        Require(field.Checks[0].State == CheckState.Unresolved && field.Checks[1].State == CheckState.Invalid);
        Require(!field.IsValid && field.ValidationErrors.Single() == "Bad email" && changes == 0);
        Require(checks.Apply(Decode("""{"version":"1.0","updateDataModel":{"surfaceId":"form","path":"/email","value":"a@b.cd"}}""")) is null);
        Require(field.IsValid && field.ValidationResults.Count == 0 && field.Checks[1].State == CheckState.Valid && changes == 1);

        static JsonElement Json(string text)
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }

        static JsonElement Run(string name, string args, out A2uiDiagnostic? diagnostic)
        {
            var arguments = Json(args).EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
            BasicFunctions.TryInvoke(name, arguments, out var result, out diagnostic);
            return result;
        }

        static bool Valid(string name, string args)
        {
            var result = Run(name, args, out var diagnostic);
            Require(diagnostic is null, diagnostic);
            return result.GetProperty("valid").GetBoolean();
        }

        static string Text(string name, string args)
        {
            var result = Run(name, args, out var diagnostic);
            Require(diagnostic is null, diagnostic);
            return result.GetString()!;
        }

        static bool Fails(string name, string args, string messagePart)
        {
            Run(name, args, out var diagnostic);
            return diagnostic?.Code == "ExpressionError" && diagnostic.Message.Contains(messagePart, StringComparison.Ordinal);
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
    }

    private static void CheckOutbound(CatalogRegistry registry)
    {
        // Two connections reuse the surface ID with different owners and data.
        const string template = """
            {"version":"1.0","createSurface":{"surfaceId":"main","catalogId":"BASIC","sendDataModel":true,
              "dataModel":{"owner":"OWNER","items":[{"id":"a"},{"id":"b"}]},
              "components":[
                {"id":"root","component":"Column","children":["save","field","list"]},
                {"id":"save","component":"Button","child":"label","action":{"event":{"name":"save",
                  "userMessage":{"@path":"/owner"},
                  "context":{"owner":{"@path":"/owner"},"literal":{"@@path":"/not/bound"}}}}},
                {"id":"label","component":"Text","text":"Save"},
                {"id":"field","component":"TextField","label":"Owner","value":{"@path":"/owner"}},
                {"id":"list","component":"Column","children":{"componentId":"item","path":"/items"}},
                {"id":"item","component":"Button","child":"label","action":{"event":{"name":"pick",
                  "context":{"id":{"@path":"id"},"position":{"@call":"@index"}}}}}]}}
            """;
        string Create(string owner) => template.Replace("BASIC", CatalogRegistry.BasicCatalogId, StringComparison.Ordinal)
            .Replace("OWNER", owner, StringComparison.Ordinal);
        using var first = new SurfaceSession(registry);
        using var second = new SurfaceSession(registry);
        Require(first.Apply(Decode(Create("one"))) is null && second.Apply(Decode(Create("two"))) is null);
        using var firstNodes = new NodeResolver(first, registry, "main", timeProvider: new FixedTime());
        using var secondNodes = new NodeResolver(second, registry, "main", timeProvider: new FixedTime());
        ResolvedNode Find(NodeResolver nodes, string component, int occurrence = 0) =>
            nodes.Nodes.Where(node => node.ComponentId == component).ElementAt(occurrence);

        // Actions read the data at invocation time, and each session keeps its own data and order.
        var save = firstNodes.Invoke(Find(firstNodes, "save"), "action");
        Require(save.Error is null && save.Message is not null);
        var saved = save.Message!.Payload;
        Require(saved.GetProperty("name").GetString() == "save" && saved.GetProperty("surfaceId").GetString() == "main" &&
            saved.GetProperty("sourceComponentId").GetString() == "save" && saved.GetProperty("userMessage").GetString() == "one" &&
            saved.GetProperty("timestamp").GetString() == "2026-11-23T10:00:00.000Z");
        Require(saved.GetProperty("context").GetProperty("owner").GetString() == "one" &&
            saved.GetProperty("context").GetProperty("literal").GetProperty("@path").GetString() == "/not/bound");

        var passiveChanges = 0;
        firstNodes.PropsChanged += _ => passiveChanges++;
        using var edit = JsonDocument.Parse("\"edited\"");
        Require(firstNodes.Write(Find(firstNodes, "field"), "value", edit.RootElement) is null && passiveChanges > 0);
        var afterEdit = firstNodes.Invoke(Find(firstNodes, "save"), "action");
        Require(afterEdit.Message!.Payload.GetProperty("context").GetProperty("owner").GetString() == "edited");

        var secondSave = secondNodes.Invoke(Find(secondNodes, "save"), "action");
        var secondPick = secondNodes.Invoke(Find(secondNodes, "item", 1), "action");
        var firstPick = firstNodes.Invoke(Find(firstNodes, "item", 0), "action");
        Require(secondSave.Message!.Payload.GetProperty("context").GetProperty("owner").GetString() == "two");
        Require(secondPick.Message!.Payload.GetProperty("context").GetProperty("id").GetString() == "b" &&
            secondPick.Message.Payload.GetProperty("context").GetProperty("position").GetInt32() == 1);
        Require(firstPick.Message!.Payload.GetProperty("context").GetProperty("id").GetString() == "a" &&
            firstPick.Message.Payload.GetProperty("context").GetProperty("position").GetInt32() == 0);

        // Only surfaces that opted in are synchronized, and only from the session that owns them.
        Require(first.Apply(Decode("""
            {"version":"1.0","createSurface":{"surfaceId":"quiet","catalogId":"BASIC","dataModel":{"secret":1}}}
            """.Replace("BASIC", CatalogRegistry.BasicCatalogId, StringComparison.Ordinal))) is null);
        var firstModels = first.GetRendererDataModel()!.Value.GetProperty("surfaces");
        Require(firstModels.EnumerateObject().Count() == 1 && firstModels.GetProperty("main").GetProperty("owner").GetString() == "edited");
        Require(second.GetRendererDataModel()!.Value.GetProperty("surfaces").GetProperty("main").GetProperty("owner").GetString() == "two");
        using (var empty = new SurfaceSession(registry))
            Require(empty.GetRendererDataModel() is null);

        // Failures become wire errors with the right code and correlation.
        Require(firstNodes.Invoke(Find(firstNodes, "label"), "text").Error?.Code == "INVALID_ACTION");
        var duplicate = Decode(Create("one"));
        var rejection = first.Apply(duplicate);
        var duplicateError = RendererError.FromRejectedMessage(duplicate, rejection!);
        var duplicateMessage = duplicateError.ToWireMessage().Payload;
        Require(duplicateError.Category == "IntegrityError" && duplicateMessage.GetProperty("code").GetString() == "INTEGRITY_ERROR" &&
            duplicateMessage.GetProperty("surfaceId").GetString() == "main");
        var malformed = Encoding.UTF8.GetBytes("""{"version":"1.0","deleteSurface":{}}""");
        Require(!WireMessage.TryDecode(malformed, WireDirection.AgentToRenderer, out _, out var malformedProblem));
        var malformedMessage = RendererError.FromRejectedEnvelope(malformed, malformedProblem!).ToWireMessage().Payload;
        Require(malformedMessage.GetProperty("code").GetString() == "VALIDATION_FAILED" &&
            malformedMessage.GetProperty("path").GetString() == "/surfaceId" && malformedMessage.GetProperty("surfaceId").GetString() == "");
        var rpc = Encoding.UTF8.GetBytes("""
            {"version":"1.0","callRendererFunction":{"functionCallId":"c1","callFunction":{"@call":"now"}}}
            """);
        Require(!WireMessage.TryDecode(rpc, WireDirection.AgentToRenderer, out _, out var rpcProblem));
        var rpcMessage = RendererError.FromRejectedEnvelope(rpc, rpcProblem!).ToWireMessage().Payload;
        Require(rpcMessage.GetProperty("code").GetString() == "INVALID_FUNCTION_CALL" &&
            rpcMessage.GetProperty("functionCallId").GetString() == "c1" && !rpcMessage.TryGetProperty("surfaceId", out _) &&
            rpcMessage.GetProperty("message").GetString()!.Contains("catalogId", StringComparison.Ordinal));
        var proto = Decode("""{"version":"1.0","updateDataModel":{"surfaceId":"main","path":"/a/__proto__","value":1}}""");
        Require(RendererError.FromRejectedMessage(proto, first.Apply(proto)!).Code == "DATA_ERROR");

        // Function actions run locally, refuse without user activation, and never emit a message.
        first.TryGetSurface("main", out var surface);
        var dispatcher = new ActionDispatcher(new FunctionEvaluator(registry));
        using var open = JsonDocument.Parse("""{"functionCall":{"@call":"openUrl","args":{"url":"https://example.test"}}}""");
        var refused = dispatcher.Dispatch(surface!, "link", "/", open.RootElement, userInitiated: false);
        Require(refused.Message is null && refused.Error?.Code == "EXPRESSION_ERROR" && refused.Error.SurfaceId == "main");
        var allowed = dispatcher.Dispatch(surface!, "link", "/", open.RootElement);
        Require(allowed.Message is null && allowed.Error is null);
        using var wrongMessage = JsonDocument.Parse("""{"event":{"name":"x","userMessage":{"@path":"/items"}}}""");
        Require(dispatcher.Dispatch(surface!, "b", "/", wrongMessage.RootElement).Error?.Code == "INVALID_ACTION");
        using var badKey = JsonDocument.Parse("""{"event":{"name":"x","context":{"k":{"@other":1}}}}""");
        var badContext = dispatcher.Dispatch(surface!, "b", "/", badKey.RootElement).Error;
        Require(badContext?.Code == "EXPRESSION_ERROR" && badContext.Message.Contains("context entry 'k'", StringComparison.Ordinal));
        using var badExtension = JsonDocument.Parse("""{"extensions":{"not-valid":"x"}}""");
        using var plain = JsonDocument.Parse("""{"event":{"name":"x"}}""");
        Require(dispatcher.Dispatch(surface!, "b", "/", plain.RootElement, metadata: badExtension.RootElement).Error?.Code == "VALIDATION_FAILED");

        // Capabilities list every registered catalog, with definitions only on request.
        var capabilities = registry.CreateRendererCapabilities().Value.GetProperty("1.0");
        Require(capabilities.GetProperty("supportedCatalogIds").EnumerateArray().Count() == registry.CatalogIds.Count &&
            !capabilities.TryGetProperty("inlineCatalogs", out _));
        Require(registry.CreateRendererCapabilities(includeInlineCatalogs: true).Value.GetProperty("1.0")
            .GetProperty("inlineCatalogs").GetArrayLength() == registry.CatalogIds.Count);
    }

    private static void CheckRpc(CatalogRegistry registry)
    {
        const string create = """
            {"version":"1.0","createSurface":{"surfaceId":"main","catalogId":"BASIC","dataModel":{"base":"EUR"}}}
            """;
        var createMessage = Decode(create.Replace("BASIC", CatalogRegistry.BasicCatalogId, StringComparison.Ordinal));
        using var first = new SurfaceSession(registry);
        using var second = new SurfaceSession(registry);
        Require(first.Apply(createMessage) is null && second.Apply(createMessage) is null);
        var firstSent = new List<WireMessage>();
        var secondSent = new List<WireMessage>();
        using var firstClient = new AgentFunctionClient(firstSent.Add);
        using var secondClient = new AgentFunctionClient(secondSent.Add);
        var evaluator = new FunctionEvaluator(registry);
        using var firstRouter = new FunctionCallRouter(first, registry, evaluator, firstClient);
        using var secondRouter = new FunctionCallRouter(second, registry, evaluator, secondClient);
        var errors = new List<RendererError>();
        firstRouter.ErrorReported += errors.Add;

        // A call the catalog does not declare goes to the agent, and equal calls on one surface share one request.
        using var rate = JsonDocument.Parse("""{"@call":"fetchRate","args":{"base":{"@path":"/base"}}}""");
        using var firstBinding = firstRouter.Bind("main", "/", rate.RootElement);
        using var sharing = firstRouter.Bind("main", "/", rate.RootElement);
        using var secondBinding = secondRouter.Bind("main", "/", rate.RootElement);
        Require(firstSent.Count == 1 && secondSent.Count == 1 && firstBinding.IsPending && firstBinding.Passes);
        var request = firstSent[0].Payload;
        Require(request.GetProperty("callFunction").GetProperty("catalogId").GetString() == CatalogRegistry.BasicCatalogId &&
            request.GetProperty("callFunction").GetProperty("args").GetProperty("base").GetString() == "EUR");

        // An answer settles only the session that sent the call, even though the surface IDs match.
        string Answer(string id, string body) =>
            """{"version":"1.0","agentFunctionResponse":{"functionCallId":"ID",BODY}}"""
                .Replace("ID", id, StringComparison.Ordinal).Replace("BODY", body, StringComparison.Ordinal);
        var answer = Decode(Answer(request.GetProperty("functionCallId").GetString()!, "\"value\":1.08"));
        Require(!secondRouter.ReceiveResponse(answer) && secondBinding.IsPending);
        Require(firstRouter.ReceiveResponse(answer) && firstBinding.Value.GetDouble() == 1.08 && sharing.Value.GetDouble() == 1.08);
        Require(!firstRouter.ReceiveResponse(answer));

        // A failed call is reported once and is not sent again until its arguments change.
        using var failing = JsonDocument.Parse("""{"@call":"fetchRate","args":{"base":{"@path":"/other"}}}""");
        using var failed = firstRouter.Bind("main", "/", failing.RootElement);
        var failedId = firstSent[1].Payload.GetProperty("functionCallId").GetString()!;
        Require(firstRouter.ReceiveResponse(Decode(Answer(failedId, "\"error\":{\"code\":\"EXECUTION_ERROR\",\"message\":\"Offline\"}"))));
        using var change = JsonDocument.Parse("1");
        Require(first.WriteData("main", "/unrelated", change.RootElement) is null && firstSent.Count == 2);
        Require(failed.HasFailed && errors.Count == 1 && errors[0].Code == "EXPRESSION_ERROR" && errors[0].FunctionCallId == failedId);
        using var dollar = JsonDocument.Parse("\"USD\"");
        Require(first.WriteData("main", "/other", dollar.RootElement) is null && firstSent.Count == 3);

        // Deleting the surface cancels the pending call without an error.
        Require(first.Apply(Decode("""{"version":"1.0","deleteSurface":{"surfaceId":"main"}}""")) is null);
        Require(firstClient.PendingCount == 0 && errors.Count == 1);

        // The agent calls a renderer function. The host function runs only where the catalog allows an agent caller.
        const string catalog = """
            {"catalogId":"test:host","protocolVersion":"1.0","components":{},"functions":{
              "ping":{"type":"object","returnType":"object","allowedCallers":"rendererOrAgent",
                "properties":{"@call":{"const":"ping"},"args":{"type":"object","properties":{"n":{"type":"number"}},"required":["n"]}},
                "required":["@call","args"]},
              "boom":{"type":"object","returnType":"any","allowedCallers":"agentOnly",
                "properties":{"@call":{"const":"boom"},"args":{"type":"object"}},"required":["@call","args"]},
              "local":{"type":"object","returnType":"any","requiresUserActivation":true,
                "properties":{"@call":{"const":"local"},"args":{"type":"object"}},"required":["@call","args"]}}}
            """;
        var host = new CatalogRegistry(includeBasicCatalog: false);
        Require(host.TryRegister(Encoding.UTF8.GetBytes(catalog), out var hostProblem), hostProblem);
        // The pinned schema permits user activation only for renderer-only functions.
        var invalidCatalog = catalog.Replace(
            "\"requiresUserActivation\":true,", "\"requiresUserActivation\":true,\"allowedCallers\":\"agentOnly\",",
            StringComparison.Ordinal);
        var invalidRegistry = new CatalogRegistry(includeBasicCatalog: false);
        Require(!invalidRegistry.TryRegister(Encoding.UTF8.GetBytes(invalidCatalog), out _));
        var hostFunctions = new FunctionEvaluator(host);
        hostFunctions.RegisterHostFunction("test:host", "ping", call =>
            JsonDocument.Parse("""{"pong":N}""".Replace("N", call.Args["n"].GetRawText(), StringComparison.Ordinal)).RootElement.Clone());
        hostFunctions.RegisterHostFunction("test:host", "boom", _ => throw new InvalidOperationException("secret"));
        var handler = new RendererFunctionHandler(host, hostFunctions);
        JsonElement Call(string name, string args, bool userActivation = false) => handler.Handle(Decode("""
            {"version":"1.0","callRendererFunction":{"functionCallId":"r1","callFunction":{"@call":"NAME","catalogId":"test:host","args":ARGS}}}
            """.Replace("NAME", name, StringComparison.Ordinal).Replace("ARGS", args, StringComparison.Ordinal)), userActivation).Payload;
        Require(Call("ping", """{"n":2}""").GetProperty("value").GetProperty("pong").GetInt32() == 2);
        Require(Call("ping", """{"n":"x"}""").GetProperty("error").GetProperty("code").GetString() == "INVALID_FUNCTION_CALL");
        Require(Call("local", "{}").GetProperty("error").GetProperty("code").GetString() == "INVALID_FUNCTION_CALL");
        Require(Call("local", "{}", userActivation: true).GetProperty("error").GetProperty("code").GetString() == "INVALID_FUNCTION_CALL");
        var boom = Call("boom", "{}").GetProperty("error");
        Require(boom.GetProperty("code").GetString() == "EXECUTION_ERROR" &&
            !boom.GetProperty("message").GetString()!.Contains("secret", StringComparison.Ordinal));

        // The pending limit is shared across surfaces and completion makes room for the next call.
        var limitedSent = new List<WireMessage>();
        using var limited = new AgentFunctionClient(limitedSent.Add, maxPendingCalls: 1);
        var function = request.GetProperty("callFunction");
        var firstCall = limited.CallAsync("main", function, "first");
        var refused = limited.CallAsync("another", function, "refused");
        Require(refused.Exception?.InnerException is AgentFunctionException { Code: "PENDING_LIMIT" });
        Require(limitedSent.Count == 1 && limited.PendingCount == 1);
        Require(limited.TryHandleResponse(Decode(Answer("first", "\"value\":1"))));
        Require(firstCall.GetAwaiter().GetResult().GetInt32() == 1);
        var nextCall = limited.CallAsync("another", function, "next");
        Require(limitedSent.Count == 2 && limited.CancelSurface("another") == 1);
        Require(nextCall.Exception?.InnerException is AgentFunctionException { Code: "CANCELLED" });

        // A call that nobody answers times out, and a late answer is ignored.
        var timed = new List<WireMessage>();
        using var timing = new AgentFunctionClient(timed.Add);
        using var slow = JsonDocument.Parse("""{"@call":"wait","catalogId":"x","args":{}}""");
        var pending = timing.CallAsync("main", slow.RootElement, "late", TimeSpan.FromMilliseconds(20));
        var timedOut = false;
        try
        {
            pending.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException failure)
        {
            timedOut = failure.InnerException is AgentFunctionException { Code: "TIMEOUT" };
        }
        Require(timedOut);
        Require(!timing.TryHandleResponse(Decode(Answer("late", "\"value\":1"))));
    }

    // A catalog with a function that has no implementation, so the renderer sends its calls to the agent.
    private const string RemoteCatalog = """
        {"catalogId":"test:remote","protocolVersion":"1.0","components":{},"functions":{
          "lookup":{"type":"object","returnType":"any",
            "properties":{"@call":{"const":"lookup"},"args":{"type":"object"}},"required":["@call","args"]}}}
        """;

    private static CatalogRegistry RemoteRegistry()
    {
        var registry = new CatalogRegistry();
        Require(registry.TryRegister(Encoding.UTF8.GetBytes(RemoteCatalog), out var problem), problem);
        return registry;
    }

    private static string Lookup(string args) => """{"@call":"lookup","catalogId":"test:remote","args":""" + args + "}";

    private static WireMessage AgentValue(WireMessage call, string value) => AgentAnswer(call, "\"value\":" + value);

    private static WireMessage AgentFailure(WireMessage call, string message) =>
        AgentAnswer(call, "\"error\":{\"code\":\"EXECUTION_ERROR\",\"message\":" + Quote(message) + "}");

    private static WireMessage AgentAnswer(WireMessage call, string body) => Decode(
        """{"version":"1.0","agentFunctionResponse":{"functionCallId":""" +
        Quote(call.Payload.GetProperty("functionCallId").GetString()!) + "," + body + "}}");

    private static string Arguments(WireMessage call) => call.Payload.GetProperty("callFunction").GetProperty("args").GetRawText();

    private static WireMessage Create(string surfaceId, string dataModel, string components) => Decode(
        """{"version":"1.0","createSurface":{"surfaceId":""" + Quote(surfaceId) + ",\"catalogId\":" +
        Quote(CatalogRegistry.BasicCatalogId) + ",\"dataModel\":" + dataModel + ",\"components\":" + components + "}}");

    private static WireMessage UpdateComponents(string surfaceId, string components) => Decode(
        """{"version":"1.0","updateComponents":{"surfaceId":""" + Quote(surfaceId) + ",\"components\":" + components + "}}");

    private static string Quote(string text) => System.Text.Json.Nodes.JsonValue.Create(text)!.ToJsonString();

    private static void CheckMountedAgentCalls()
    {
        var registry = RemoteRegistry();
        using var session = new SurfaceSession(registry);
        var sent = new List<WireMessage>();
        var errors = new List<RendererError>();
        using var client = new AgentFunctionClient(sent.Add);
        using var router = new FunctionCallRouter(session, registry, new FunctionEvaluator(registry), client);
        router.ErrorReported += errors.Add;
        using var resolver = new NodeResolver(session, registry, "form", router);
        var changed = new List<string>();
        resolver.PropsChanged += node => changed.Add(node.ComponentId);
        ResolvedNode Node(string id, int occurrence = 0) => resolver.Nodes.Where(node => node.ComponentId == id).ElementAt(occurrence);
        WireMessage Sent(string args) => sent.Last(call => Arguments(call) == args);

        Require(session.Apply(Create("form", """{"user":"ada","items":[{"id":"a"},{"id":"b"}]}""",
            """[{"id":"root","component":"Column","children":["greeting","name","list"]},""" +
            """{"id":"greeting","component":"Text","text":""" + Lookup("""{"user":{"@path":"/user"}}""") + "}," +
            """{"id":"name","component":"TextField","label":"Name","value":{"@path":"/user"},"@checks":[{"condition":""" +
            Lookup("""{"check":{"@path":"/user"}}""") + ""","message":"Taken"}]},""" +
            """{"id":"list","component":"Column","children":{"componentId":"item","path":"/items"}},""" +
            """{"id":"item","component":"Text","text":""" + Lookup("""{"id":{"@path":"id"}}""") + "}]")) is null);

        // Every agent call is sent at mount. Properties wait without a value and pending checks do not block.
        Require(sent.Count == 4 && client.PendingCount == 4);
        var greeting = Node("greeting");
        var name = Node("name");
        Require(greeting.Props["text"].Value is null && greeting.Props["text"].Kind == ResolvedPropertyKind.Binding);
        Require(name.Checks.Single().State == CheckState.Pending && name.IsValid && changed.Count == 0);

        // Answers update only the node that waited, once.
        Require(router.ReceiveResponse(AgentValue(Sent("""{"user":"ada"}"""), "\"Hello Ada\"")));
        Require(greeting.Props["text"].Value?.GetString() == "Hello Ada" && changed.SequenceEqual(["greeting"]));
        Require(router.ReceiveResponse(AgentValue(Sent("""{"check":"ada"}"""), """{"valid":false,"message":"In use"}""")));
        Require(name.Checks[0].State == CheckState.Invalid && !name.IsValid && name.ValidationErrors.Single() == "Taken");
        Require(changed.SequenceEqual(["greeting", "name"]) && errors.Count == 0);

        // A data change sends the calls whose arguments changed. A failed call leaves the value empty and the check invalid.
        Require(session.WriteData("form", "/user", Json("\"lin\"")) is null && sent.Count == 6);
        Require(greeting.Props["text"].Value is null && name.Checks[0].State == CheckState.Pending && name.IsValid);
        var failed = Sent("""{"user":"lin"}""");
        Require(router.ReceiveResponse(AgentFailure(failed, "Offline")));
        Require(greeting.Props["text"].Value is null && errors.Count == 1 && errors[0].Code == "EXPRESSION_ERROR" &&
            errors[0].FunctionCallId == failed.Payload.GetProperty("functionCallId").GetString());
        Require(router.ReceiveResponse(AgentValue(Sent("""{"check":"lin"}"""), "true")));
        Require(name.Checks[0].State == CheckState.Valid && name.IsValid);

        // Detaching a node cancels its pending call. Its late answer is dropped and raises nothing.
        var second = Node("item", 1);
        var secondCall = Sent("""{"id":"b"}""");
        var pending = client.PendingCount;
        Require(session.WriteData("form", "/items", Json("""[{"id":"a"}]""")) is null);
        Require(!resolver.Nodes.Contains(second) && client.PendingCount == pending - 1);
        var before = changed.Count;
        Require(!router.ReceiveResponse(AgentValue(secondCall, "\"late\"")) && changed.Count == before);
        Require(Node("item").Props["text"].Value is null && errors.Count == 1);

        // A component update that detaches a subtree cancels the calls of every detached node.
        var firstCall = Sent("""{"id":"a"}""");
        Require(session.Apply(UpdateComponents("form", """[{"id":"root","component":"Column","children":["greeting","name"]}]""")) is null);
        Require(client.PendingCount == 0 && !router.ReceiveResponse(AgentValue(firstCall, "\"late\"")));

        // Two nodes share one call. Detaching one keeps the call for the other, and a later node reads the cached answer.
        var sharedLookup = Lookup("""{"key":"same"}""");
        Require(session.Apply(UpdateComponents("form",
            """[{"id":"root","component":"Column","children":["greeting","name","first","second"]},""" +
            """{"id":"first","component":"Text","text":""" + sharedLookup + "}," +
            """{"id":"second","component":"Text","text":""" + sharedLookup + "}]")) is null);
        var shared = sent[^1];
        Require(Arguments(shared) == """{"key":"same"}""" && client.PendingCount == 1);
        Require(session.Apply(UpdateComponents("form", """[{"id":"root","component":"Column","children":["greeting","name","first"]}]""")) is null);
        Require(client.PendingCount == 1 && router.ReceiveResponse(AgentValue(shared, "\"kept\"")));
        Require(Node("first").Props["text"].Value?.GetString() == "kept");
        var sentBefore = sent.Count;
        Require(session.Apply(UpdateComponents("form", """[{"id":"root","component":"Column","children":["greeting","name","first","second"]}]""")) is null);
        Require(Node("second").Props["text"].Value?.GetString() == "kept" && sent.Count == sentBefore);

        // Deleting the surface cancels pending calls without an error, and their late answers are dropped.
        Require(session.Apply(UpdateComponents("form",
            """[{"id":"third","component":"Text","text":""" + Lookup("""{"key":"third"}""") + "}," +
            """{"id":"root","component":"Column","children":["greeting","name","third"]}]""")) is null);
        var third = sent[^1];
        Require(client.PendingCount == 1);
        Require(session.Apply(Decode("""{"version":"1.0","deleteSurface":{"surfaceId":"form"}}""")) is null);
        Require(client.PendingCount == 0 && resolver.Nodes.Count == 0 && errors.Count == 1);
        Require(!router.ReceiveResponse(AgentValue(third, "\"late\"")));

        // Mounted actions run through the router and can wait for the agent.
        Require(session.Apply(Create("actions", "{}",
            """[{"id":"root","component":"Column","children":["save"]},""" +
            """{"id":"save","component":"Button","child":"label","action":{"event":{"name":"save","context":{"quote":""" +
            Lookup("""{"item":"x"}""") + "}}}}," +
            """{"id":"label","component":"Text","text":"Save"}]""")) is null);
        using var actions = new NodeResolver(session, registry, "actions", router);
        var dispatched = new List<WireMessage>();
        router.ActionDispatched += dispatched.Add;
        var save = actions.Nodes.Single(node => node.ComponentId == "save");
        Require(actions.RouteAction(save, "action") is null && dispatched.Count == 0);
        Require(router.ReceiveResponse(AgentValue(sent[^1], "12")));
        Require(dispatched.Single().Payload.GetProperty("context").GetProperty("quote").GetInt32() == 12 &&
            dispatched[0].Payload.GetProperty("sourceComponentId").GetString() == "save");
        var label = actions.Nodes.Single(node => node.ComponentId == "label");
        Require(actions.RouteFunctionAction(label, "text", _ => { })?.Code == "INVALID_ACTION");

        using var local = new NodeResolver(session, registry, "actions");
        var refused = false;
        try
        {
            local.RouteAction(local.Nodes[0], "action");
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }
        Require(refused);
        using var otherSession = new SurfaceSession(registry);
        var mismatch = false;
        try
        {
            using var wrong = new NodeResolver(otherSession, registry, "actions", router);
        }
        catch (ArgumentException)
        {
            mismatch = true;
        }
        Require(mismatch);

        // An in-process agent can answer inside the send callback. The node resolves at mount.
        FunctionCallRouter? inline = null;
        using var inlineSession = new SurfaceSession(registry);
        using var inlineClient = new AgentFunctionClient(call => inline!.ReceiveResponse(
            Arguments(call).Contains("fail", StringComparison.Ordinal) ? AgentFailure(call, "No") : AgentValue(call, "\"now\"")));
        inline = new FunctionCallRouter(inlineSession, registry, new FunctionEvaluator(registry), inlineClient);
        using (inline)
        using (var inlineNodes = new NodeResolver(inlineSession, registry, "inline", inline))
        {
            Require(inlineSession.Apply(Create("inline", "{}",
                """[{"id":"root","component":"TextField","label":"Now","value":""" + Lookup("""{"when":"now"}""") +
                ""","@checks":[{"condition":""" + Lookup("""{"when":"fail"}""") + ""","message":"Failed"}]}]""")) is null);
            var field = inlineNodes.Nodes.Single();
            Require(field.Props["value"].Value?.GetString() == "now" && field.Checks[0].State == CheckState.Invalid &&
                inlineClient.PendingCount == 0);
        }
    }

    private static void CheckMountedDisposalOrder()
    {
        var registry = RemoteRegistry();
        var components = """[{"id":"root","component":"TextField","label":"Name","value":""" + Lookup("""{"field":"value"}""") +
            ""","@checks":[{"condition":""" + Lookup("""{"field":"check"}""") + ""","message":"Bad"}]}]""";

        // Disposing the resolver first cancels its calls. The router keeps working for other bindings.
        using (var session = new SurfaceSession(registry))
        {
            var sent = new List<WireMessage>();
            using var client = new AgentFunctionClient(sent.Add);
            using var router = new FunctionCallRouter(session, registry, new FunctionEvaluator(registry), client);
            var resolver = new NodeResolver(session, registry, "order", router);
            var changes = 0;
            resolver.PropsChanged += _ => changes++;
            Require(session.Apply(Create("order", "{}", components)) is null && client.PendingCount == 2);
            resolver.Dispose();
            Require(client.PendingCount == 0 && resolver.Nodes.Count == 0);
            Require(sent.All(call => !router.ReceiveResponse(AgentValue(call, "true"))) && changes == 0);
            using var binding = router.Bind("order", "/", Json(Lookup("""{"field":"value"}""")));
            Require(binding.IsPending && client.PendingCount == 1);
            resolver.Dispose();
        }

        // Disposing the router first cancels its calls. The resolver keeps refreshing and resolves those calls locally.
        using (var session = new SurfaceSession(registry))
        {
            var sent = new List<WireMessage>();
            using var client = new AgentFunctionClient(sent.Add);
            var router = new FunctionCallRouter(session, registry, new FunctionEvaluator(registry), client);
            using var resolver = new NodeResolver(session, registry, "order", router);
            Require(session.Apply(Create("order", """{"n":1}""", components)) is null && client.PendingCount == 2);
            router.Dispose();
            Require(client.PendingCount == 0);
            Require(session.WriteData("order", "/n", Json("2")) is null);
            var field = resolver.Nodes.Single();
            Require(field.Props["value"].Value is null && field.Checks[0].State == CheckState.Unresolved && field.IsValid);
            Require(sent.All(call => !client.TryHandleResponse(AgentValue(call, "true"))));
        }
    }

    // Hosts may receive answers on worker threads. They serialise each answer with the session, as for any other call.
    private static void CheckMountedConcurrentReplies()
    {
        var registry = RemoteRegistry();
        using var session = new SurfaceSession(registry);
        var sent = new List<WireMessage>();
        using var client = new AgentFunctionClient(sent.Add);
        using var router = new FunctionCallRouter(session, registry, new FunctionEvaluator(registry), client);
        using var resolver = new NodeResolver(session, registry, "load", router);
        var gate = new object();
        var violations = 0;
        resolver.PropsChanged += node =>
        {
            if (!Monitor.IsEntered(gate) || !resolver.Nodes.Contains(node))
                Interlocked.Increment(ref violations);
        };

        const int count = 40;
        string Items(IEnumerable<int> ids) => "[" + string.Join(",", ids.Select(id => "{\"id\":" + id + "}")) + "]";
        lock (gate)
            Require(session.Apply(Create("load", "{\"items\":" + Items(Enumerable.Range(0, count)) + "}",
                """[{"id":"root","component":"Column","children":{"componentId":"item","path":"/items"}},""" +
                """{"id":"item","component":"Text","text":""" + Lookup("""{"id":{"@path":"id"}}""") + "}]")) is null);
        var calls = sent.ToArray();
        Require(calls.Length == count);

        // Removing the odd items moves the even items, so their nodes rebind while answers arrive on other threads.
        var replies = Task.Run(() => Parallel.ForEach(calls, new ParallelOptions { MaxDegreeOfParallelism = 4 }, call =>
        {
            var id = Json(Arguments(call)).GetProperty("id").GetInt32();
            lock (gate)
                router.ReceiveResponse(AgentValue(call, Quote("item-" + id)));
        }));
        lock (gate)
            Require(session.WriteData("load", "/items", Json(Items(Enumerable.Range(0, count).Where(id => id % 2 == 0)))) is null);
        Require(replies.Wait(TimeSpan.FromSeconds(30)));

        lock (gate)
        {
            var mounted = resolver.Nodes.Where(node => node.ComponentId == "item").ToArray();
            Require(mounted.Length == count / 2 && client.PendingCount == 0 && violations == 0);
            for (var index = 0; index < mounted.Length; index++)
                Require(mounted[index].Props["text"].Value?.GetString() == $"item-{index * 2}");
        }
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static void CheckOccurrences(CatalogRegistry registry)
    {
        using var session = new SurfaceSession(registry);
        var create = Decode($$$"""
            {"version":"1.0","createSurface":{"surfaceId":"list","catalogId":"{{{CatalogRegistry.BasicCatalogId}}}",
              "dataModel":{"items":[{"children":[{"name":"Ada"}]}]},
              "components":[{"id":"root","component":"Column","children":{"componentId":"group","path":"/items"}},
                {"id":"group","component":"Column","children":{"componentId":"name","path":"children"}},
                {"id":"name","component":"Text","text":{"@path":"name"}}]}}
            """);
        Require(session.Apply(create) is null);
        var root = session.GetComponentOccurrences("list");
        var name = root!.Children.Single().Children.Single();
        Require(name.DataPath == "/items/0/children/0");
        Require(session.TryGetSurface("list", out var snapshot) &&
            snapshot!.TryGetScopedData(name.DataPath, "name", out var value) && value.GetString() == "Ada");
        using var edit = JsonDocument.Parse("\"Grace\"");
        Require(session.WriteScopedData("list", name.DataPath, "name", edit.RootElement) is null);
        Require(session.GetComponentOccurrences("list")!.Children[0].Children[0].InstanceId == name.InstanceId);
        Require(session.Apply(Decode("""{"version":"1.0","deleteSurface":{"surfaceId":"list"}}""")) is null);
        Require(session.GetComponentOccurrences("list") is null);
    }

    private static void CheckDataWrites(SurfaceSession session)
    {
        var changes = new List<string?>();
        session.Changed += change => changes.Add(change.DataPath);
        using (var edit = JsonDocument.Parse("""{"local":true,"value":null}"""))
            Require(session.WriteScopedData("form", "/", "edit", edit.RootElement) is null);

        Require(session.TryGetSurface("form", out var edited) &&
            edited!.TryGetScopedData("/edit", "value", out var value) && value.ValueKind == JsonValueKind.Null);
        Require(!edited!.TryGetScopedData("/edit", "missing", out _));
        Require(edited.TryGetScopedData("/edit", "", out var scope) && scope.GetProperty("local").GetBoolean());
        Require(edited.TryGetScopedData("/edit", "/name", out var name) && name.GetString() == "Ada");
        Require(!edited.TryGetScopedData("/edit", "~2", out _));
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

    // Covers behaviour that data_model.yaml does not pin: value ownership, refused writes, list slots, escaped keys,
    // observer lifetime and the shared pointer rules of the wire path.
    private static void CheckDataModel(CatalogRegistry registry)
    {
        static JsonElement Json(string text)
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }

        static JsonElement Read(DataModel model, string path)
        {
            Require(model.Get(path, out var value) is null);
            return value;
        }

        var model = new DataModel();
        var released = JsonDocument.Parse("""{"id":9007199254740993,"tags":["a"]}""");
        Require(model.Set("/user", released.RootElement) is null);
        released.Dispose();
        Require(Read(model, "/user/id").GetRawText() == "9007199254740993");
        var rootDocument = JsonDocument.Parse("""{"kept":true}""");
        Require(model.Set("/", rootDocument.RootElement) is null);
        rootDocument.Dispose();
        Require(model.Value.GetRawText() == """{"kept":true}""");

        model = new DataModel(Json("""{"user":{"name":"Ada"},"items":["a","b"],"slot":null,"a/b":{"x":1}}"""));
        var changes = new List<string>();
        Require(model.Observe("/user", _ => changes.Add("user"), out var user) is null);
        Require(model.Observe("/a~1b", _ => changes.Add("escaped"), out var escaped) is null);
        Require(model.Observe("/a/b", _ => changes.Add("nested"), out var nested) is null);

        // A refused write leaves the model as it was, with no containers created on the way and no notifications.
        var before = model.Value.GetRawText();
        Require(model.Set("/user/name/first/deep", Json("1"))?.Code == "invalidDataPath");
        Require(model.Set("/items/10001", Json("1"))?.Code == "invalidDataPath");
        Require(model.Set("/items/-1", Json("1"))?.Code == "invalidDataPath");
        Require(model.Set("/bad/~2", Json("1"))?.Code == "invalidDataPath");
        Require(model.Set("relative", Json("1"))?.Code == "invalidDataPath");
        Require(model.Set("/user/__proto__", Json("1"))?.Code == "forbiddenPathKey");
        Require(model.Delete("/user/constructor")?.Code == "forbiddenPathKey");
        Require(model.Observe("/prototype", _ => { }, out var rejected)?.Code == "forbiddenPathKey" && rejected is null);
        Require(model.Value.GetRawText() == before);
        Require(changes.Count == 0);

        // Observers compare whole segments after unescaping, so /a~1b is neither /a/b nor an ancestor of it.
        Require(model.Set("/a~1b/x", Json("2")) is null && changes.SequenceEqual(new[] { "escaped" }));
        changes.Clear();
        Require(model.Set("/a/b", Json("1")) is null && changes.SequenceEqual(new[] { "nested" }));
        changes.Clear();
        Require(model.Set("/a/b", Json("1.0")) is null && changes.Count == 0);
        Require(nested!.Value.GetRawText() == "1");

        // A null slot becomes a container on write. Deleting a list element keeps the list length.
        Require(model.Set("/slot/value", Json("true")) is null && Read(model, "/slot/value").GetBoolean());
        Require(model.Delete("/items/0") is null && Read(model, "/items").GetRawText() == "[null,\"b\"]");
        Require(model.Set("/items/3", Json("\"d\"")) is null && Read(model, "/items").GetRawText() == "[null,\"b\",null,\"d\"]");
        Require(model.Delete("/user/name") is null && Read(model, "/user").GetRawText() == "{}");
        Require(model.Delete("/user/name/first") is null && model.Delete("/items/9") is null && Read(model, "/items").GetArrayLength() == 4);
        Require(model.Get("/user/name", out var absent) is null && absent.ValueKind == JsonValueKind.Undefined);
        Require(user!.Value.GetRawText() == "{}");

        // Disposing one subscription stops only that observer, more than once is harmless, and a callback may write.
        changes.Clear();
        escaped!.Dispose();
        escaped.Dispose();
        Require(model.Set("/a~1b/x", Json("3")) is null && changes.Count == 0);
        var nestedWrites = 0;
        Require(model.Observe("/trigger", value =>
        {
            nestedWrites++;
            if (value.GetInt32() == 1)
                Require(model.Set("/trigger", Json("2")) is null);
        }, out var trigger) is null);
        Require(model.Set("/trigger", Json("1")) is null && nestedWrites == 2 && trigger!.Value.GetInt32() == 2);

        // Model disposal detaches everything but leaves the data usable.
        model.Dispose();
        changes.Clear();
        Require(model.Set("/user/name", Json("\"Bo\"")) is null && changes.Count == 0 && Read(model, "/user/name").GetString() == "Bo");
        try
        {
            model.Observe("/user", _ => { }, out _);
            throw new InvalidOperationException("Expected an observer on a disposed model to be refused.");
        }
        catch (ObjectDisposedException)
        {
        }

        // The wire path shares the pointer rules: lists pad, deleting an absent value does nothing, and null slots are replaced.
        using var session = new SurfaceSession(registry);
        Require(session.Apply(Decode($$$$"""
            {"version":"1.0","createSurface":{"surfaceId":"data","catalogId":"{{{{CatalogRegistry.BasicCatalogId}}}}",
              "dataModel":{"items":["a"],"slot":null}}}
            """)) is null);
        Require(session.Apply(Decode("""
            {"version":"1.0","updateDataModel":{"surfaceId":"data","path":"/items/2","value":"c"}}
            """)) is null && session.TryGetSurface("data", out var padded) &&
            padded!.DataModel.GetProperty("items").GetRawText() == "[\"a\",null,\"c\"]");
        Require(session.Apply(Decode("""
            {"version":"1.0","updateDataModel":{"surfaceId":"data","path":"/items/10001","value":"x"}}
            """))?.Code == "invalidDataPath");
        Require(session.Apply(Decode("""
            {"version":"1.0","updateDataModel":{"surfaceId":"data","path":"/items/40","value":null}}
            """)) is null && session.TryGetSurface("data", out var kept) && kept!.DataModel.GetProperty("items").GetArrayLength() == 3);
        Require(session.Apply(Decode("""
            {"version":"1.0","updateDataModel":{"surfaceId":"data","path":"/slot/inner","value":1}}
            """)) is null && session.TryGetSurface("data", out var filled) &&
            filled!.DataModel.GetProperty("slot").GetRawText() == """{"inner":1}""");
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
