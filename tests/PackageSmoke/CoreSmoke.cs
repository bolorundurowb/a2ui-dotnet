using System.Buffers;
using System.Text;
using System.Text.Json;
using A2UI.Core;

internal static class CoreSmoke
{
    internal static void Run()
    {
        var registry = new CatalogRegistry();
        CheckOccurrences(registry);
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
        CheckExpressionScope();
        CheckNodeResolver(registry);
        CheckFunctions(registry);

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
