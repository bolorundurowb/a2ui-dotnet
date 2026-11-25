using System.Globalization;
using System.Text.Json;

namespace A2UI.Core;

/// <summary>Controls which incomplete component graphs a surface can commit.</summary>
public sealed record GraphValidationOptions
{
    /// <summary>Allows updates before the root component arrives.</summary>
    public bool AllowMissingRoot { get; init; }
    /// <summary>Allows references to components that have not arrived.</summary>
    public bool AllowDanglingReferences { get; init; }
    /// <summary>Allows components that cannot be reached from the root.</summary>
    public bool AllowOrphanComponents { get; init; }

    /// <summary>Requires a complete root tree for each nonempty component batch.</summary>
    public static GraphValidationOptions Strict { get; } = new();
    /// <summary>Allows incomplete trees while still checking cycles, depth and composition.</summary>
    public static GraphValidationOptions Progressive { get; } = new()
    {
        AllowMissingRoot = true,
        AllowDanglingReferences = true,
        AllowOrphanComponents = true
    };
}

internal static class ComponentGraph
{
    internal static A2uiDiagnostic? Validate(SurfaceSnapshot snapshot, CatalogRegistry catalogs,
        GraphValidationOptions options, string path, Dictionary<string, string> updatePaths,
        CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        var hasIndexCall = false;
        foreach (var (id, component) in snapshot.Components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var edges = new List<Edge>();
            var componentPath = updatePaths.GetValueOrDefault(id, path);
            if (!catalogs.TryGetComponentSchema(component, snapshot.CatalogId, out var document, out var schema))
                return new("unknownCatalogItem", componentPath, "Component schema is unavailable.");
            var type = component.GetProperty("component").GetString()!;
            Collect(component, schema, document, componentPath, edges, 0, catalogs, snapshot.CatalogId);
            var indexCallPath = FindIndexCall(component);
            hasIndexCall |= indexCallPath is not null;
            nodes.Add(id, new(type, schema, edges, componentPath, indexCallPath));
        }

        var root = snapshot.RootComponentId;
        if (nodes.Count == 0)
            return null;
        if (nodes.TryGetValue(root, out var rootNode) && !Allows(rootNode.Schema, "allowedParents", "Surface"))
            return DisallowedParent(root, rootNode, "Surface", "Surface");
        foreach (var (id, node) in nodes)
        {
            foreach (var edge in node.Edges)
            {
                if (nodes.TryGetValue(edge.Id, out var child))
                {
                    if (!Allows(node.Schema, "allowedChildren", child.Type))
                        return new("unallowedChild", edge.Path, $"Container '{id}' ({node.Type}) cannot contain child " +
                            $"'{edge.Id}' ({child.Type}). Allowed children: {AllowedTypes(node.Schema, "allowedChildren")}.");
                    if (!Allows(child.Schema, "allowedParents", node.Type))
                        return DisallowedParent(edge.Id, child, id, node.Type);
                }
                else if (!options.AllowDanglingReferences)
                    return new("danglingReference", edge.Path, $"Dangling reference to unavailable component {edge.Id}.");
            }
        }
        if (rootNode is null && !options.AllowMissingRoot)
            return new("missingRoot", path, "Missing root component.");

        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        if (rootNode is not null && Visit(root, nodes, depths, path, 0, cancellationToken) is { } error)
            return error;
        foreach (var (id, node) in nodes)
        {
            if (depths.ContainsKey(id))
                continue;
            if (!options.AllowOrphanComponents)
                return new("orphanComponent", node.Path, $"Component '{id}' is not reachable from '{root}'.");
            if (Visit(id, nodes, depths, path, 0, cancellationToken) is { } orphanError)
                return orphanError;
        }
        return hasIndexCall && rootNode is not null
            ? ValidateIndexScope(root, nodes, new(StringComparer.Ordinal), cancellationToken)
            : null;
    }

    private static A2uiDiagnostic DisallowedParent(string id, Node node, string parent, string parentType) =>
        new("unallowedParent", node.Path, $"Component '{id}' ({node.Type}) cannot be placed under parent " +
            $"'{parent}' ({parentType}). Allowed parents: {AllowedTypes(node.Schema, "allowedParents")}.");

    private static string AllowedTypes(JsonElement schema, string rule) =>
        "[" + string.Join(", ", schema.GetProperty(rule).EnumerateArray().Select(item => $"'{item.GetString()}'")) + "]";

    private static A2uiDiagnostic? ValidateIndexScope(string id, Dictionary<string, Node> nodes,
        HashSet<string> visited, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!nodes.TryGetValue(id, out var node) || !visited.Add(id))
            return null;
        if (node.IndexCallPath is { } path)
            return new("indexOutsideCollection", node.Path + path,
                "@index function can only be evaluated inside a collection template iteration scope.");
        foreach (var edge in node.Edges)
            if (edge.TemplatePath is null &&
                ValidateIndexScope(edge.Id, nodes, visited, cancellationToken) is { } error)
                return error;
        return null;
    }

    private static string? FindIndexCall(JsonElement value)
    {
        if (WireJson.IsObject(value))
        {
            if (value.TryGetProperty("@call", out var call) &&
                call.ValueKind == JsonValueKind.String && call.ValueEquals("@index"))
                return "";
            foreach (var property in value.EnumerateObject())
                if (FindIndexCall(property.Value) is { } found)
                    return WireJson.Path("", property.Name) + found;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (FindIndexCall(item) is { } found)
                    return $"/{index.ToString(CultureInfo.InvariantCulture)}{found}";
                index++;
            }
        }
        return null;
    }

    private static A2uiDiagnostic? Visit(string id, Dictionary<string, Node> nodes, Dictionary<string, int> depths,
        string path, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > WireJson.RecursionLimit)
            return new("graphDepth", path, "Global recursion limit exceeded.");
        if (depths.TryGetValue(id, out var cached))
        {
            if (cached < 0)
                return new("circularReference", path, $"Circular component reference at '{id}'.");
            return depth + cached > WireJson.RecursionLimit ? new("graphDepth", path, "Global recursion limit exceeded.") : null;
        }

        depths[id] = -1;
        var height = 0;
        foreach (var edge in nodes[id].Edges)
        {
            if (!nodes.ContainsKey(edge.Id))
                continue;
            if (id == edge.Id)
                return new("circularReference", edge.Path, "Self-reference detected.");
            if (Visit(edge.Id, nodes, depths, edge.Path, depth + 1, cancellationToken) is { } error)
                return error;
            height = Math.Max(height, depths[edge.Id] + 1);
        }
        depths[id] = height;
        return null;
    }

    internal static IReadOnlyList<Edge> GetChildren(JsonElement component, SurfaceSnapshot surface, CatalogRegistry catalogs)
    {
        var edges = new List<Edge>();
        if (catalogs.TryGetComponentSchema(component, surface.CatalogId, out var document, out var schema))
            Collect(component, schema, document, "", edges, 0, catalogs, surface.CatalogId);
        return edges;
    }

    private static bool Allows(JsonElement schema, string rule, string type) =>
        !WireJson.IsObject(schema) || !schema.TryGetProperty(rule, out var allowed) ||
        allowed.EnumerateArray().Any(value => value.ValueEquals(type));

    private static void Collect(JsonElement value, JsonElement schema, JsonElement document,
        string path, List<Edge> edges, int depth, CatalogRegistry catalogs, string? surfaceCatalogId)
    {
        if (depth > 64 || !WireJson.IsObject(schema))
            return;
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var parts = reference.GetString()!.Split('#', 2);
            if (parts.Length == 2 && ProtocolSchemas.IsCommonTypes(parts[0]))
            {
                switch (parts[1])
                {
                    case "/$defs/Child" or "/$defs/ComponentId" when value.ValueKind == JsonValueKind.String:
                        edges.Add(new(value.GetString()!, path));
                        break;
                    case "/$defs/ChildList" when value.ValueKind == JsonValueKind.Array:
                        var index = 0;
                        foreach (var item in value.EnumerateArray())
                        {
                            var itemPath = WireJson.Path(path, (index++).ToString(CultureInfo.InvariantCulture));
                            if (item.ValueKind == JsonValueKind.String)
                                edges.Add(new(item.GetString()!, itemPath));
                        }
                        break;
                    case "/$defs/ChildList" when value.ValueKind == JsonValueKind.Object &&
                        value.TryGetProperty("componentId", out var id) && id.ValueKind == JsonValueKind.String:
                        edges.Add(new(id.GetString()!, WireJson.Path(path, "componentId"), value.GetProperty("path").GetString()));
                        break;
                }
            }
            else if (parts.Length == 2 && SchemaValidator.TryResolvePointer(document, parts[1], out var target))
                Collect(value, target, document, path, edges, depth + 1, catalogs, surfaceCatalogId);
        }

        if (WireJson.IsObject(value) && schema.TryGetProperty("properties", out var properties))
            foreach (var property in value.EnumerateObject())
                if (properties.TryGetProperty(property.Name, out var propertySchema))
                    Collect(property.Value, propertySchema, document, WireJson.Path(path, property.Name), edges,
                        depth + 1, catalogs, surfaceCatalogId);

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                Collect(item, items, document, WireJson.Path(path, (index++).ToString(CultureInfo.InvariantCulture)),
                    edges, depth + 1, catalogs, surfaceCatalogId);
        }
        foreach (var combiner in new[] { "allOf", "anyOf", "oneOf" })
            if (schema.TryGetProperty(combiner, out var branches))
                foreach (var branch in branches.EnumerateArray())
                    if (combiner == "allOf" || catalogs.MatchesComponentBranch(value, branch, document, surfaceCatalogId))
                        Collect(value, branch, document, path, edges, depth + 1, catalogs, surfaceCatalogId);
    }

    private sealed record Node(string Type, JsonElement Schema, List<Edge> Edges, string Path, string? IndexCallPath);
    internal sealed record Edge(string Id, string Path, string? TemplatePath = null);
}
