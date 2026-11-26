using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2UI.Core;

// Builds the self-contained schema for a registered catalog. Components lose their allOf mixins and gain the `id`
// envelope, every reference to the shared types becomes a local `#/$defs/` reference, and the shared types that the
// catalog reaches are copied into `$defs`. A reference to a shared type takes that type's description when it has none
// of its own, so a tool that reads the schema sees what each property holds.
internal sealed class CatalogValidationSchema
{
    private const string DraftUri = "https://json-schema.org/draft/2020-12/schema";
    private const string DefinitionsPrefix = "/$defs/";

    private readonly JsonObject _source;
    private readonly JsonObject _common;
    private readonly JsonObject _local;
    private readonly string? _sourceId;
    private readonly HashSet<string> _referenced = new(StringComparer.Ordinal);

    private CatalogValidationSchema(JsonObject source)
    {
        _source = source;
        _common = JsonNode.Parse(ProtocolSchemas.CommonTypes.GetRawText())!["$defs"]!.AsObject();
        _local = source["$defs"] as JsonObject ?? new JsonObject();
        _sourceId = source["$id"]?.GetValue<string>();
    }

    internal static JsonElement Create(JsonElement definition) =>
        ProtocolSchemas.Parse(new CatalogValidationSchema(JsonNode.Parse(definition.GetRawText())!.AsObject()).Build().ToJsonString());

    private JsonObject Build()
    {
        var schema = new JsonObject { ["$schema"] = _source["$schema"]?.DeepClone() ?? DraftUri };
        foreach (var member in _source)
            if (member.Key is not ("$id" or "$schema" or "title" or "description" or "components" or "functions" or "$defs"))
                schema[member.Key] = member.Value?.DeepClone();

        if (_source["components"] is JsonObject components)
        {
            var flattened = new JsonObject();
            foreach (var component in components)
                flattened[component.Key] = Flatten(component.Value!.AsObject());
            schema["components"] = flattened;
        }

        if (_source["functions"] is JsonObject functions)
        {
            var rewritten = (JsonObject)functions.DeepClone();
            Rewrite(rewritten, commonContext: false, hoist: true, schemaMap: true);
            schema["functions"] = rewritten;
        }

        schema["$defs"] = BuildDefinitions();
        return schema;
    }

    private JsonObject BuildDefinitions()
    {
        var definitions = (JsonObject)_local.DeepClone();
        Rewrite(definitions, commonContext: false, hoist: false, schemaMap: true);
        definitions["anyComponent"] ??= UnionOf("components", discriminated: true);
        definitions["anyFunction"] ??= UnionOf("functions", discriminated: false);

        // Copying a shared type can reach further shared types, so repeat until nothing new is referenced.
        while (_referenced.FirstOrDefault(candidate => !definitions.ContainsKey(candidate) && _common.ContainsKey(candidate)) is { } name)
        {
            var copy = _common[name]!.DeepClone();
            Rewrite(copy, commonContext: true, hoist: false);
            definitions[name] = copy;
        }
        return definitions;
    }

    private JsonObject UnionOf(string group, bool discriminated)
    {
        var references = new JsonArray();
        if (_source[group] is JsonObject entries)
            foreach (var entry in entries)
                references.Add((JsonNode)new JsonObject { ["$ref"] = "#" + WireJson.Path(WireJson.Path("", group), entry.Key) });
        if (references.Count == 0)
            return new JsonObject { ["not"] = new JsonObject() };

        var union = new JsonObject { ["oneOf"] = references };
        if (discriminated)
            union["discriminator"] = new JsonObject { ["propertyName"] = "component" };
        return union;
    }

    private JsonObject Flatten(JsonObject component)
    {
        var properties = new JsonObject();
        var required = new List<string> { "id" };
        var keywords = new JsonObject();

        var id = new JsonObject { ["$ref"] = "common_types.json#/$defs/ComponentId" };
        Rewrite(id, commonContext: false, hoist: true);
        properties["id"] = id;

        Merge(component, commonContext: false, properties, required, keywords, isRoot: true);
        keywords["type"] ??= "object";
        keywords["properties"] = properties;
        keywords["required"] = new JsonArray(required.Distinct(StringComparer.Ordinal).Select(name => (JsonNode)name).ToArray());
        return keywords;
    }

    // A later schema wins over an earlier one, so a component's own properties override those of its mixins.
    private void Merge(JsonObject schema, bool commonContext, JsonObject properties, List<string> required,
        JsonObject keywords, bool isRoot)
    {
        if (schema["allOf"] is JsonArray branches)
            foreach (var branch in branches.OfType<JsonObject>())
                MergeBranch(branch, commonContext, properties, required, keywords);

        if (schema["properties"] is JsonObject own)
            foreach (var property in own)
            {
                if (commonContext && property.Key == "@catalogId")
                    continue;
                var copy = property.Value!.DeepClone();
                Rewrite(copy, commonContext, hoist: true);
                properties[property.Key] = copy;
            }

        if (schema["required"] is JsonArray names)
            required.AddRange(names.Select(name => name!.GetValue<string>()));

        if (!isRoot)
            return;
        foreach (var keyword in schema)
        {
            if (keyword.Key is "allOf" or "properties" or "required")
                continue;
            var copy = keyword.Value?.DeepClone();
            Rewrite(copy, commonContext, hoist: true);
            keywords[keyword.Key] = copy;
        }
    }

    private void MergeBranch(JsonObject branch, bool commonContext, JsonObject properties, List<string> required,
        JsonObject keywords)
    {
        if (branch["$ref"]?.GetValue<string>() is not { } reference)
        {
            Merge(branch, commonContext, properties, required, keywords, isRoot: false);
            return;
        }

        var parts = reference.Split('#', 2);
        var name = DefinitionName(parts.Length == 2 ? parts[1] : "");
        if (name is null)
            return;
        if (ProtocolSchemas.IsCommonTypes(parts[0]) || (parts[0].Length == 0 && commonContext))
        {
            if (_common[name] is JsonObject shared)
                Merge(shared, commonContext: true, properties, required, keywords, isRoot: false);
        }
        else if (parts[0].Length == 0 && _local[name] is JsonObject local)
            Merge(local, commonContext: false, properties, required, keywords, isRoot: false);
    }

    // The common context applies when the node was copied from the shared types, where a bare "#/$defs/" reference
    // names a shared type. Keywords that hold instance data are left alone.
    private void Rewrite(JsonNode? node, bool commonContext, bool hoist, bool schemaMap = false)
    {
        if (node is JsonArray items)
        {
            foreach (var item in items)
                Rewrite(item, commonContext, hoist);
            return;
        }

        if (node is not JsonObject members)
            return;
        if (!schemaMap && members["$ref"]?.GetValue<string>() is { } reference)
            members["$ref"] = Retarget(members, reference, commonContext, hoist);

        foreach (var member in members.ToArray())
        {
            if (!schemaMap && member.Key is "default" or "examples" or "const" or "enum")
                continue;
            Rewrite(member.Value, commonContext, hoist,
                schemaMap: !schemaMap && member.Key is "properties" or "patternProperties" or "$defs" or "components" or "functions");
        }
    }

    private string Retarget(JsonObject schema, string reference, bool commonContext, bool hoist)
    {
        var parts = reference.Split('#', 2);
        var fragment = parts.Length == 2 ? parts[1] : "";
        var name = DefinitionName(fragment);
        var shared = ProtocolSchemas.IsCommonTypes(parts[0]) || (parts[0].Length == 0 && commonContext);
        var target = reference;
        if (ProtocolSchemas.IsCommonTypes(parts[0]) || parts[0] == "catalog.json" || (_sourceId is not null && parts[0] == _sourceId))
            target = "#" + fragment;

        if (name is not null)
            _referenced.Add(name);
        if (hoist && shared && name is not null && !schema.ContainsKey("description") &&
            _common[name]?["description"] is JsonValue description && description.TryGetValue<string>(out var text))
            schema["description"] = text;
        return target;
    }

    private static string? DefinitionName(string fragment) =>
        fragment.StartsWith(DefinitionsPrefix, StringComparison.Ordinal) && fragment.Length > DefinitionsPrefix.Length &&
        fragment.IndexOf('/', DefinitionsPrefix.Length) < 0
            ? fragment[DefinitionsPrefix.Length..]
            : null;
}
