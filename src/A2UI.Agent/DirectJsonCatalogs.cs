using System.Text.Json.Nodes;
using A2UI.Core;

namespace A2UI.Agent;

public static partial class DirectJson
{
    private static A2uiDiagnostic? QualifyCatalogItems(JsonObject envelope, CatalogRegistry catalogs,
        Dictionary<string, bool> scopedSurfaces, RendererState? state)
    {
        foreach (var kind in new[] { "createSurface", "updateComponents" })
        {
            if (envelope[kind] is not JsonObject payload)
                continue;
            if (payload["catalogId"] is JsonValue surfaceCatalog &&
                surfaceCatalog.TryGetValue<string>(out var catalogId) &&
                !catalogs.TryGetDefinition(catalogId, out _))
                return Invalid("catalogId", catalogId);
            var scoped = payload.ContainsKey("catalogId");
            if (payload["surfaceId"] is JsonValue surface && surface.TryGetValue<string>(out var surfaceId))
            {
                if (kind == "createSurface")
                    scopedSurfaces[surfaceId] = scoped;
                else if (!scopedSurfaces.TryGetValue(surfaceId, out scoped))
                    scoped = state is not null && state.TryGetSurface(surfaceId, out var snapshot) &&
                        snapshot!.CatalogId is not null;
            }
            // A surface with an explicit default can resolve its unqualified items.
            if (scoped || payload["components"] is not JsonArray components)
                continue;
            foreach (var entry in components)
            {
                if (entry is not JsonObject component)
                    continue;
                // Some inference fixtures use the function spelling for component overrides.
                if (!component.ContainsKey("@catalogId") && component["catalogId"] is JsonValue)
                {
                    var legacyId = component["catalogId"];
                    component.Remove("catalogId");
                    component["@catalogId"] = legacyId;
                }
                if (Qualify(component, "components", "component", "@catalogId") is { } issue)
                    return issue;
                foreach (var property in component)
                    if (property.Key != "@metadata" && QualifyCalls(property.Value) is { } callIssue)
                        return callIssue;
            }
        }

        if (envelope["callRendererFunction"] is JsonObject rpc && rpc["callFunction"] is JsonObject call)
            return Qualify(call, "functions", "@call", "catalogId");
        return null;

        A2uiDiagnostic? QualifyCalls(JsonNode? value)
        {
            if (value is JsonObject members)
            {
                if (members["@call"] is JsonValue name && name.TryGetValue<string>(out var functionName) &&
                    functionName != "@index" &&
                    Qualify(members, "functions", "@call", "catalogId") is { } issue)
                    return issue;
                foreach (var member in members)
                    if (QualifyCalls(member.Value) is { } nested)
                        return nested;
            }
            else if (value is JsonArray items)
                foreach (var item in items)
                    if (QualifyCalls(item) is { } nested)
                        return nested;
            return null;
        }

        A2uiDiagnostic? Qualify(JsonObject item, string group, string nameKey, string catalogKey)
        {
            if (item.ContainsKey(catalogKey) || item[nameKey] is not JsonValue name ||
                !name.TryGetValue<string>(out var itemName))
                return null;

            string? match = null;
            foreach (var id in catalogs.CatalogIds)
            {
                if (!catalogs.TryGetDefinition(id, out var definition) ||
                    !definition.TryGetProperty(group, out var entries) ||
                    !entries.TryGetProperty(itemName, out _))
                    continue;
                if (match is not null)
                    return Invalid(catalogKey, $"'{itemName}' is ambiguous across active catalogs. Specify {catalogKey}.");
                match = id;
            }

            if (match is null)
                return Invalid(nameKey, $"'{itemName}' is unavailable in the active catalogs.");
            item[catalogKey] = match;
            return null;
        }
    }
}
