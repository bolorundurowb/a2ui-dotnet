using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>A Direct JSON prompt and its corresponding monolithic skill.</summary>
public sealed class DirectJsonPrompt
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string _text;

    private DirectJsonPrompt(string text) => _text = text;

    /// <summary>The prompt snippet generated from the negotiated catalog definitions.</summary>
    public string Text => _text;

    /// <summary>Renders this prompt as a self-contained skill document.</summary>
    /// <returns>Markdown with skill frontmatter and the generated instructions.</returns>
    public string ToSkill() => "---\nname: a2ui-direct-json\ndescription: Generate A2UI 1.0 Direct JSON responses.\n---\n\n" + Text.Trim() + "\n";

    /// <summary>Creates a file containing the generated monolithic skill.</summary>
    /// <returns>The skill document and its default export path.</returns>
    public SkillFile ToSkillFile() => new("a2ui-direct-json/SKILL.md", ToSkill());

    /// <summary>Creates a prompt for the active catalogs and validates its examples.</summary>
    /// <param name="catalogs">The active catalog definitions in negotiation order.</param>
    /// <param name="prompt">The rendered prompt on success.</param>
    /// <param name="examples">Optional example message batches, in presentation order.</param>
    /// <param name="allowedMessages">Optional allowlist of envelope kinds.</param>
    /// <returns>A diagnostic on invalid catalogs or examples.</returns>
    public static A2uiDiagnostic? Create(CatalogRegistry catalogs, out DirectJsonPrompt? prompt,
        IEnumerable<string>? examples = null, IEnumerable<string>? allowedMessages = null)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        prompt = null;
        if (catalogs.CatalogIds.Count == 0)
            return new("CatalogError", "", "At least one active catalog is required.");
        var documents = new List<JsonObject>();
        foreach (var id in catalogs.CatalogIds)
        {
            catalogs.TryGetDefinition(id, out var definition);
            documents.Add(JsonNode.Parse(definition.GetRawText())!.AsObject());
        }
        var protocol = JsonNode.Parse(ProtocolSchemas.AgentToRenderer.GetRawText())!.AsObject();
        var allow = allowedMessages?.ToHashSet(StringComparer.Ordinal);
        if (allow is not null)
        {
            var defs = protocol["$defs"]!.AsObject();
            var roots = defs.Where(p => p.Value?["properties"] is JsonObject props && props.Any(k => allow.Contains(k.Key)))
                .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            var branches = protocol["oneOf"]!.AsArray();
            for (var i = branches.Count - 1; i >= 0; i--)
                if (!roots.Contains(branches[i]!["$ref"]!.GetValue<string>().Split('/')[^1]))
                    branches.RemoveAt(i);
            Prune(defs, roots);
        }
        var common = JsonNode.Parse(ProtocolSchemas.CommonTypes.GetRawText())!.AsObject();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents.Append(protocol))
            Collect(document, used);
        Prune(common["$defs"]!.AsObject(), used);
        var sections = new List<string> { "### Agent to Renderer Schema:\n" + protocol.ToJsonString(Options) };
        if (common["$defs"]!.AsObject().Count != 0)
            sections.Add("### Common Types Schema:\n" + common.ToJsonString(Options));

        using var stream = typeof(DirectJsonPrompt).Assembly.GetManifestResourceStream("A2UI.Agent.Upstream.Workflow.txt")!;
        using var reader = new StreamReader(stream);
        var blocks = new List<string> { reader.ReadToEnd().ReplaceLineEndings("\n"), Block(sections) };
        foreach (var document in documents)
            blocks.Add(Block(["### Catalog Schema:\n" + document.ToJsonString(Options)]));
        if (examples is not null)
        {
            var rendered = new List<string>();
            foreach (var example in examples)
            {
                if (DirectJson.Compile(example, catalogs, out var batch) is { } error)
                    return error;
                if (allow is not null && batch.Any(message => !allow.Contains(message.Kind)))
                    return new("ValidationError", "", "Example uses a disabled message kind.");
                rendered.Add(DirectJson.Wrap([new DirectJsonPart(DirectJson.Decompile(batch).ReplaceLineEndings("\n"), true)]));
            }
            if (rendered.Count != 0)
                blocks.Add("### Examples:\n\n" + string.Join("\n\n", rendered));
        }
        prompt = new(string.Join("\n\n", blocks));
        return null;
    }

    private static string Block(IEnumerable<string> sections) =>
        "---BEGIN A2UI JSON SCHEMA---\n\n" + string.Join("\n\n", sections) + "\n\n---END A2UI JSON SCHEMA---";

    private static void Collect(JsonNode? value, HashSet<string> names)
    {
        if (value is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out var id) &&
                (id.StartsWith("#/$defs/", StringComparison.Ordinal) || id.Contains("common_types.json#/$defs/", StringComparison.Ordinal)))
                names.Add(id.Split('/')[^1]);
            foreach (var property in obj)
                Collect(property.Value, names);
        }
        else if (value is JsonArray array)
            foreach (var item in array)
                Collect(item, names);
    }

    private static void Prune(JsonObject defs, HashSet<string> used)
    {
        var queue = new Queue<string>(used);
        while (queue.TryDequeue(out var name))
        {
            var references = new HashSet<string>(StringComparer.Ordinal);
            Collect(defs[name], references);
            foreach (var reference in references)
                if (used.Add(reference))
                    queue.Enqueue(reference);
        }
        foreach (var key in defs.Select(k => k.Key).Where(key => !used.Contains(key)).ToArray())
            defs.Remove(key);
    }
}
