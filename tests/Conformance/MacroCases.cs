using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Agent;
using A2UI.Core;

// Runs the upstream macro suite through the public MacroExpander and Core entry points.
internal static class MacroCases
{
    internal const string Path = "conformance/agent/macros/macros.yaml";

    private const string BasicAlias = "org.a2ui.basic";

    // A case names its protocol version in envelope_version. The upstream suite defaults to v0.9, which this SDK does not support.
    // Twin cases use version, and catalog export cases that name only the expander use expander_protocol_version.
    internal static string Version(JsonElement test) =>
        Read(test, "envelope_version") ?? Read(test, "expander_protocol_version") ?? Read(test, "version") ?? "v0.9";

    internal static bool IsLegacy(JsonElement test) => Normalise(Version(test)) != WireMessage.Version;

    internal static void Run(JsonElement test, JsonElement document, string assets)
    {
        var diagnostic = test.TryGetProperty("action", out var action)
            ? RunCatalogAction(test, document, assets, action.GetString()!)
            : RunTransport(test, document, assets);
        if (test.TryGetProperty("expect_error", out var expected))
        {
            var category = expected.GetProperty("category").GetString();
            Require(diagnostic is not null && diagnostic.Code == category,
                $"Expected {category} but got {diagnostic?.ToString() ?? "success"}.");
        }
        else
            Require(diagnostic is null, diagnostic?.ToString() ?? "Unexpected failure.");
    }

    private static A2uiDiagnostic? RunCatalogAction(JsonElement test, JsonElement document, string assets, string action)
    {
        var diagnostic = CreateExpander(test, document, assets, out var expander);
        if (diagnostic is not null)
            return diagnostic;
        var expect = test.TryGetProperty("expect", out var found) ? found : default;
        string definition;
        switch (action)
        {
            case "to_catalog":
                diagnostic = expander!.CreateCatalog(Read(test, "expander_catalog_id"), out definition);
                break;
            case "transform_to_inference_catalog":
            case "transform_catalog":
                var allowed = test.TryGetProperty("passthrough_components", out var list)
                    ? list.EnumerateArray().Select(item => item.GetString()!).ToArray() : null;
                diagnostic = expander!.AugmentCatalog(CatalogRegistry.BasicCatalogId, "https://example.com/inference.json", allowed,
                    out definition);
                break;
            default:
                throw new InvalidDataException($"Unknown macro action {action}.");
        }

        if (diagnostic is null && expect.ValueKind == JsonValueKind.Object)
            CheckCatalog(JsonNode.Parse(definition)!.AsObject(), expect);
        return diagnostic;
    }

    private static void CheckCatalog(JsonObject catalog, JsonElement expect)
    {
        var components = catalog["components"]!.AsObject();
        var references = (catalog["$defs"]?["anyComponent"]?["oneOf"] as JsonArray)?
            .Select(item => item!["$ref"]!.GetValue<string>()).ToArray() ?? [];
        foreach (var item in expect.EnumerateObject())
        {
            switch (item.Name)
            {
                case "catalog_id":
                    Require(catalog["catalogId"]!.GetValue<string>() == item.Value.GetString(), "Catalog ID differs.");
                    break;
                case "protocol_version":
                    Require(catalog["protocolVersion"]!.GetValue<string>() == item.Value.GetString(), "Protocol version differs.");
                    break;
                case "augmented_components":
                    foreach (var name in Names(item.Value))
                        Require(components.ContainsKey(name), $"Missing component {name}.");
                    break;
                case "absent_components":
                    foreach (var name in Names(item.Value))
                        Require(!components.ContainsKey(name), $"Unexpected component {name}.");
                    break;
                case "any_component_references":
                    foreach (var name in Names(item.Value))
                        Require(references.Contains(name), $"Missing reference {name}.");
                    break;
                case "absent_any_component_references":
                    foreach (var name in Names(item.Value))
                        Require(!references.Contains("#/components/" + name), $"Unexpected reference {name}.");
                    break;
                case "component_properties":
                    foreach (var component in item.Value.EnumerateObject())
                        foreach (var property in component.Value.EnumerateObject())
                            Require(Same(components[component.Name]?["properties"]?[property.Name], ToNode(property.Value)),
                                $"Schema of {component.Name}.{property.Name} differs.");
                    break;
                case "has_any_component_discriminator":
                    Require(catalog["$defs"]?["anyComponent"]?["discriminator"] is not null == item.Value.GetBoolean(),
                        "The discriminator differs.");
                    break;
                case "has_any_function":
                    Require(catalog["$defs"]?["anyFunction"] is not null == item.Value.GetBoolean(), "anyFunction differs.");
                    break;
                case "component_count":
                    Require(components.Count == item.Value.GetInt32(), "Component count differs.");
                    break;
                default:
                    throw new InvalidDataException($"Unknown macro catalog expectation {item.Name}.");
            }
        }

        static IEnumerable<string> Names(JsonElement array) => array.EnumerateArray().Select(name => name.GetString()!);
    }

    private static A2uiDiagnostic? RunTransport(JsonElement test, JsonElement document, string assets)
    {
        var diagnostic = CreateExpander(test, document, assets, out var expander);
        if (diagnostic is not null)
            return diagnostic;
        var surfaceId = Read(test, "surface_id") ?? "conformance";
        var messages = new List<WireMessage>();
        foreach (var envelope in Envelopes(test, surfaceId))
        {
            Require(WireMessage.TryDecode(Encoding.UTF8.GetBytes(envelope.ToJsonString()), WireDirection.AgentToRenderer,
                out var message, out var decodeError), decodeError?.ToString() ?? "Decode failed.");
            messages.Add(message!);
        }

        Dictionary<string, string?>? surfaceCatalogs = null;
        if (test.TryGetProperty("surface_catalogs", out var overrides))
            surfaceCatalogs = overrides.EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetString());
        diagnostic = expander!.Expand(messages, out var expanded, surfaceCatalogs);
        if (diagnostic is not null)
            return diagnostic;

        var actual = new JsonArray(expanded.Select(message => JsonNode.Parse(message.Envelope.GetRawText())).ToArray());
        if (test.TryGetProperty("golden", out var golden))
        {
            var path = System.IO.Path.Combine(assets, "conformance", "agent", "macros", "golden", golden.GetString()!);
            Require(Same(actual, JsonNode.Parse(File.ReadAllText(path))), "The expansion differs from the golden output.");
        }

        if (test.TryGetProperty("expect", out var expect))
            CheckTransport(actual, expect);
        var skipValidation = test.TryGetProperty("validation", out var validation) &&
            validation.TryGetProperty("skip_renderer_validation", out var skip) && skip.GetBoolean();
        return skipValidation ? null : Validate(test, document, assets, actual, surfaceId);
    }

    private static IEnumerable<JsonNode> Envelopes(JsonElement test, string surfaceId)
    {
        if (test.TryGetProperty("messages", out var sequence))
        {
            foreach (var message in sequence.EnumerateArray())
                yield return ToNode(message)!;
            yield break;
        }

        var input = test.GetProperty("input");
        var components = new JsonArray();
        foreach (var component in input.ValueKind == JsonValueKind.Array ? input.EnumerateArray().ToArray() : [input])
            components.Add(ToNode(component));
        if (Read(test, "catalog_id") is { } catalogId)
            yield return new JsonObject
            {
                ["version"] = WireMessage.Version,
                ["createSurface"] = new JsonObject { ["surfaceId"] = surfaceId, ["catalogId"] = catalogId }
            };
        yield return new JsonObject
        {
            ["version"] = WireMessage.Version,
            ["updateComponents"] = new JsonObject { ["surfaceId"] = surfaceId, ["components"] = components }
        };
    }

    private static IEnumerable<JsonObject> AllComponents(JsonArray messages) =>
        messages.Select(message => message!["updateComponents"]?["components"] as JsonArray).Where(list => list is not null)
            .SelectMany(list => list!).Select(component => component!.AsObject());

    private static void CheckTransport(JsonArray messages, JsonElement expect)
    {
        var components = AllComponents(messages).ToArray();
        foreach (var item in expect.EnumerateObject())
        {
            switch (item.Name)
            {
                case "components":
                    Require(Same(new JsonArray(components.Select(component => component.DeepClone()).ToArray()), ToNode(item.Value)),
                        "The expanded components differ.");
                    break;
                case "component_types":
                    Require(components.Select(component => component["component"]!.GetValue<string>())
                        .SequenceEqual(item.Value.EnumerateArray().Select(type => type.GetString()!)), "The component types differ.");
                    break;
                case "absent_property_on_all_components":
                    var property = item.Value.GetString()!;
                    Require(components.All(component => !component.ContainsKey(property) && !component.ContainsKey("@" + property)),
                        $"A component has {property}.");
                    break;
                case "create_surface_catalog_id":
                    var created = messages.Select(message => message!["createSurface"]).First(payload => payload is not null)!;
                    Require(created["catalogId"]?.GetValue<string>() == item.Value.GetString(), "The createSurface catalog differs.");
                    break;
                default:
                    throw new InvalidDataException($"Unknown macro expectation {item.Name}.");
            }
        }
    }

    // The expansion has to pass the same graph checks that a renderer applies.
    private static A2uiDiagnostic? Validate(JsonElement test, JsonElement document, string assets, JsonArray actual, string surfaceId)
    {
        var ids = Strings(test, "validation_catalogs") ?? [CatalogRegistry.BasicCatalogId];
        var registry = Registry(document, assets, ids, Read(test, "catalog_id"));
        var profile = test.TryGetProperty("validation", out var settings) ? settings : default;
        using var session = new SurfaceSession(registry, new GraphValidationOptions
        {
            AllowMissingRoot = Flag(profile, "allow_missing_root"),
            AllowDanglingReferences = Flag(profile, "allow_dangling_references"),
            // Without a root, no component can be reachable from it.
            AllowOrphanComponents = Flag(profile, "allow_orphan_components") || Flag(profile, "allow_missing_root")
        });

        var created = actual.Select(message => message!["createSurface"]?["surfaceId"]?.GetValue<string>())
            .Where(id => id is not null).ToHashSet();
        var surfaceCatalogs = test.TryGetProperty("surface_catalogs", out var overrides) ? overrides : default;
        var envelopes = actual.Select(message => message!).ToList();
        foreach (var id in AllSurfaces(actual).Where(id => !created.Contains(id)))
        {
            var catalogId = surfaceCatalogs.ValueKind == JsonValueKind.Object && surfaceCatalogs.TryGetProperty(id, out var chosen)
                ? chosen.GetString() : Read(test, "catalog_id") ?? CatalogRegistry.BasicCatalogId;
            envelopes.Insert(0, new JsonObject
            {
                ["version"] = WireMessage.Version,
                ["createSurface"] = new JsonObject { ["surfaceId"] = id, ["catalogId"] = catalogId }
            });
        }

        foreach (var envelope in envelopes)
        {
            if (!WireMessage.TryDecode(Encoding.UTF8.GetBytes(envelope.ToJsonString()), WireDirection.AgentToRenderer,
                    out var message, out var decodeError))
                return decodeError;
            if (session.Apply(message!) is { } problem)
                return problem with { Code = "ValidationError" };
        }

        return null;

        static bool Flag(JsonElement profile, string name) =>
            profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty(name, out var value) && value.GetBoolean();
    }

    private static IEnumerable<string> AllSurfaces(JsonArray messages) =>
        messages.Select(message => message!["updateComponents"]?["surfaceId"]?.GetValue<string>())
            .Where(id => id is not null).Distinct()!;

    private static A2uiDiagnostic? CreateExpander(JsonElement test, JsonElement document, string assets, out MacroExpander? expander)
    {
        var ids = Strings(test, "base_catalogs") ?? [CatalogRegistry.BasicCatalogId];
        var registry = Registry(document, assets, ids, Read(test, "catalog_id"));
        var scope = Read(test, "macro_catalog_id");
        var caseVersion = Normalise(Read(test, "macro_version") ?? Version(test));
        var macros = new List<MacroDefinition>();
        if (!(test.TryGetProperty("empty_macros", out var empty) && empty.GetBoolean()))
        {
            foreach (var entry in document.GetProperty("macros").EnumerateArray())
            {
                var owner = Read(entry, "catalog_id");
                if (owner is null || owner == scope)
                    macros.Add(Macro(entry, owner, Normalise(Read(entry, "protocol_version") ?? caseVersion)));
            }
        }

        if (Read(test, "colliding_macro") is { } name)
        {
            var colliding = MacroDefinition.FromTemplate(name, [], new JsonObject
            {
                ["component"] = "Card",
                ["child"] = new JsonObject { ["component"] = "Text", ["text"] = "Collision" }
            });
            macros.Add(new MacroDefinition(colliding.Name, colliding.Parameters, colliding.Body) { ProtocolVersion = caseVersion });
        }

        var options = new MacroExpanderOptions
        {
            ErrorMode = Read(test, "on_error") == "raise" ? MacroErrorMode.Raise : MacroErrorMode.Passthrough
        };
        return MacroExpander.Create(macros, registry, out expander, options);
    }

    private static MacroDefinition Macro(JsonElement entry, string? catalogId, string protocolVersion)
    {
        var parameters = new List<MacroParameter>();
        foreach (var item in entry.GetProperty("parameters").EnumerateObject())
        {
            var declaration = item.Value;
            parameters.Add(new MacroParameter(item.Name, ParameterType(declaration.GetProperty("type").GetString()!))
            {
                Required = !declaration.TryGetProperty("required", out var required) || required.GetBoolean(),
                DefaultValue = declaration.TryGetProperty("default", out var fallback) ? ToNode(fallback) : null,
                AllowedValues = declaration.TryGetProperty("enum", out var values)
                    ? values.EnumerateArray().Select(value => value.GetString()!).ToArray() : null
            });
        }

        var template = ToNode(entry.GetProperty("template"))!.AsObject();
        // The pinned 1.0 Basic Catalog has no heading variants. The suite's templates still use the v0.9 names.
        RewriteHeadings(template);
        var built = MacroDefinition.FromTemplate(entry.GetProperty("name").GetString()!, parameters, template);
        var body = built.Body;
        // The suite describes some behaviour only as comments beside the template, so the harness supplies it.
        if (built.Name is "StatusBadge" or "AnnotatedBadge")
            body = invocation =>
            {
                if (invocation.Arguments["status"] is JsonValue status && status.TryGetValue<string>(out var text))
                    invocation.Arguments["status"] = text.ToUpperInvariant();
                return built.Body(invocation);
            };
        else if (built.Name == "PositiveCountCard")
            body = invocation => invocation.Arguments["count"]!.GetValue<long>() < 0
                ? MacroResult.Failure("The count must not be negative.")
                : built.Body(invocation);
        return new MacroDefinition(built.Name, built.Parameters, body)
        {
            CatalogId = catalogId,
            ProtocolVersion = protocolVersion,
            Description = Read(entry, "description")
        };
    }

    private static void RewriteHeadings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject members:
                if (members["variant"] is JsonValue value && value.TryGetValue<string>(out var variant) &&
                    variant.Length == 2 && variant[0] == 'h' && char.IsDigit(variant[1]))
                    members["variant"] = "body";
                foreach (var member in members)
                    RewriteHeadings(member.Value);
                break;
            case JsonArray items:
                foreach (var item in items)
                    RewriteHeadings(item);
                break;
        }
    }

    private static MacroParameterType ParameterType(string name) => name switch
    {
        "string" => MacroParameterType.String,
        "integer" => MacroParameterType.Integer,
        "number" => MacroParameterType.Number,
        "boolean" => MacroParameterType.Boolean,
        "component" => MacroParameterType.Component,
        "component_list" => MacroParameterType.ComponentList,
        "child_list" => MacroParameterType.ChildList,
        "action" => MacroParameterType.Action,
        "dynamic_string" => MacroParameterType.DynamicString,
        "dynamic_number" => MacroParameterType.DynamicNumber,
        "dynamic_boolean" => MacroParameterType.DynamicBoolean,
        "dynamic_string_list" => MacroParameterType.DynamicStringList,
        "dynamic_value" => MacroParameterType.DynamicValue,
        "check_rule_list" => MacroParameterType.CheckRuleList,
        "accessibility" => MacroParameterType.Accessibility,
        _ => throw new InvalidDataException($"Unknown macro parameter type {name}.")
    };

    // The registry holds the Basic Catalog, the custom catalogs of the suite that the case names, and the Basic Catalog alias the suite uses.
    private static CatalogRegistry Registry(JsonElement document, string assets, IEnumerable<string> ids, string? surfaceCatalogId)
    {
        var registry = new CatalogRegistry(includeBasicCatalog: false);
        var wanted = ids.ToList();
        if (surfaceCatalogId == BasicAlias && !wanted.Contains(BasicAlias))
            wanted.Add(BasicAlias);
        foreach (var id in wanted)
        {
            JsonNode definition;
            if (id is CatalogRegistry.BasicCatalogId or BasicAlias)
            {
                definition = JsonNode.Parse(File.ReadAllBytes(System.IO.Path.Combine(assets, "catalogs/basic/v1/catalog.json")))!;
                definition["catalogId"] = id;
                definition["$id"] = id;
            }
            else
                definition = JsonNode.Parse(document.GetProperty("custom_catalogs").GetProperty(id).GetRawText().Replace(
                    "https://a2ui.org/specification/v1_0/json/common_types.json#", "common_types.json#", StringComparison.Ordinal))!;

            Require(registry.TryRegister(Encoding.UTF8.GetBytes(definition.ToJsonString()), out var error),
                $"Catalog {id} was rejected: {error}");
        }

        return registry;
    }

    private static string[]? Strings(JsonElement test, string name) =>
        test.TryGetProperty(name, out var list) ? list.EnumerateArray().Select(item => item.GetString()!).ToArray() : null;

    private static string? Read(JsonElement value, string key) =>
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string Normalise(string version) => version.TrimStart('v');

    private static JsonNode? ToNode(JsonElement value) => JsonNode.Parse(value.GetRawText());

    // Objects match whatever their key order, and numbers match by value, so 2 and 2.0 are the same.
    private static bool Same(JsonNode? actual, JsonNode? expected)
    {
        switch (actual, expected)
        {
            case (null, null):
                return true;
            case (JsonObject left, JsonObject right):
                return left.Count == right.Count && left.All(entry => right.TryGetPropertyValue(entry.Key, out var other) &&
                    Same(entry.Value, other));
            case (JsonArray left, JsonArray right):
                return left.Count == right.Count && left.Zip(right).All(pair => Same(pair.First, pair.Second));
            case (JsonValue left, JsonValue right):
                var leftKind = left.GetValueKind();
                if (leftKind == JsonValueKind.Number && right.GetValueKind() == JsonValueKind.Number)
                    return left.GetValue<double>() == right.GetValue<double>();
                return JsonNode.DeepEquals(left, right);
            default:
                return false;
        }
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new InvalidDataException(description);
    }
}
