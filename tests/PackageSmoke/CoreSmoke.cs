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
        CheckOutbound(registry);
        CheckRpc(registry);

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
