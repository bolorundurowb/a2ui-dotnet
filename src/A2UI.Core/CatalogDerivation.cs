using System.Text.Json;

namespace A2UI.Core;

/// <summary>Names the component or function entries a derived catalog contains.</summary>
/// <remarks>
/// <see cref="Keep"/> selects the source catalog's entry of that name. <see cref="Define"/> adds a new entry, or replaces
/// the source's entry of the same name.
/// </remarks>
public readonly struct CatalogEntry
{
    private CatalogEntry(string name, JsonElement? schema)
    {
        Name = name;
        Schema = schema;
    }

    /// <summary>Gets the component or function name.</summary>
    public string Name { get; }

    /// <summary>Gets the new definition, or null when the entry keeps the source catalog's definition.</summary>
    public JsonElement? Schema { get; }

    /// <summary>Selects the source catalog's entry of the given name.</summary>
    /// <param name="name">The component or function name.</param>
    /// <returns>The entry selection.</returns>
    public static CatalogEntry Keep(string name) => new(name, null);

    /// <summary>Adds an entry to the derived catalog, replacing any source entry with the same name.</summary>
    /// <param name="name">The component or function name.</param>
    /// <param name="schema">The definition object. The derived catalog holds its own copy.</param>
    /// <returns>The entry definition.</returns>
    public static CatalogEntry Define(string name, JsonElement schema) => new(name, schema);
}

/// <summary>Describes the entries and instructions of a catalog derived from a registered catalog.</summary>
public sealed class CatalogDerivation
{
    /// <summary>Gets the component entries, or null to keep every component of the source.</summary>
    public IEnumerable<CatalogEntry>? Components { get; init; }

    /// <summary>Gets the function entries, or null to keep every function of the source.</summary>
    public IEnumerable<CatalogEntry>? Functions { get; init; }

    /// <summary>Gets the instructions of the derived catalog. The source's instructions are never inherited.</summary>
    public string? Instructions { get; init; }
}
