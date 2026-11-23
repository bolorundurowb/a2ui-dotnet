using System.Globalization;
using System.Text.Json;

namespace A2UI.Core;

/// <summary>A component occurrence with an instance ID, mounted position and data scope.</summary>
/// <param name="InstanceId">The ID of the occurrence within its live surface.</param>
/// <param name="ComponentId">The component definition ID.</param>
/// <param name="ComponentType">The catalog component type.</param>
/// <param name="DataPath">The absolute pointer to the occurrence's data scope.</param>
/// <param name="Definition">The immutable component definition.</param>
/// <param name="Children">The mounted children in declaration order.</param>
/// <param name="Position">The mounted position, such as <c>root/children/0</c>.</param>
public sealed record ComponentOccurrence(string InstanceId, string ComponentId, string ComponentType,
    string DataPath, JsonElement Definition, IReadOnlyList<ComponentOccurrence> Children, string Position);

internal static class ComponentOccurrences
{
    internal static ComponentOccurrence? Resolve(SurfaceSnapshot surface, CatalogRegistry catalogs,
        int limit, CancellationToken cancellationToken)
    {
        return Mount(surface.RootComponentId, "/root", "/", 0, "root");

        ComponentOccurrence? Mount(string componentId, string instanceId, string scope, int depth, string position)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!surface.Components.TryGetValue(componentId, out var component))
                return null;
            if (--limit < 0 || depth > 50)
                throw new InvalidOperationException("The component occurrence limit was exceeded.");

            var children = new List<ComponentOccurrence>();
            var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var edge in ComponentGraph.GetChildren(component, surface, catalogs).Distinct())
            {
                if (edge.TemplatePath is null)
                {
                    var identity = WireJson.Path(WireJson.Path(instanceId, "child"), edge.Id);
                    var ordinal = ordinals.GetValueOrDefault(identity);
                    ordinals[identity] = ordinal + 1;
                    Add(WireJson.Path(identity, ordinal.ToString(CultureInfo.InvariantCulture)), scope,
                        position + edge.Path);
                    continue;
                }

                if (!SurfaceSnapshot.TryResolveScope(scope, edge.TemplatePath, out var path) ||
                    !surface.TryGetData(path, out var list) || list.ValueKind != JsonValueKind.Array ||
                    !surface.Components.ContainsKey(edge.Id))
                    continue;

                var templateId = WireJson.Path(WireJson.Path(WireJson.Path(instanceId, "template"), edge.Path), edge.Id);
                // The template edge path ends in "componentId"; the item index replaces that segment.
                var listPosition = position + edge.Path[..^"/componentId".Length];
                for (var i = 0; i < list.GetArrayLength(); i++)
                {
                    var index = i.ToString(CultureInfo.InvariantCulture);
                    Add(WireJson.Path(templateId, index), WireJson.Path(path == "/" ? "" : path, index),
                        listPosition + "/" + index);
                }

                void Add(string childInstanceId, string childScope, string childPosition)
                {
                    if (Mount(edge.Id, childInstanceId, childScope, depth + 1, childPosition) is { } child)
                        children.Add(child);
                }
            }

            return new(instanceId, componentId, component.GetProperty("component").GetString()!,
                scope, component, children.AsReadOnly(), position);
        }
    }
}
