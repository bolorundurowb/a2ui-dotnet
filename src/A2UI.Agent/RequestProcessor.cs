using System.Text.Json;
using A2UI.Core;

namespace A2UI.Agent;

/// <summary>A Direct JSON request configuration binding the same negotiated catalogs to prompts and parsing.</summary>
public sealed class RequestProcessor
{
    private readonly CatalogRegistry _catalogs;

    private RequestProcessor(CatalogRegistry catalogs, DirectJsonPrompt prompt) =>
        (_catalogs, Prompt) = (catalogs, prompt);

    /// <summary>The prompt for the negotiated or fixed catalogs.</summary>
    public DirectJsonPrompt Prompt { get; }

    /// <summary>The selected catalogs in preference order.</summary>
    public IReadOnlyCollection<string> CatalogIds => _catalogs.CatalogIds;

    /// <summary>Returns a selected catalog definition, including any pruning.</summary>
    /// <param name="id">Catalog identifier.</param>
    /// <param name="definition">The active definition on success.</param>
    /// <returns>Whether the catalog was selected.</returns>
    public bool TryGetCatalog(string id, out JsonElement definition) => _catalogs.TryGetDefinition(id, out definition);

    /// <summary>Starts an isolated mirror for processing responses in one conversation.</summary>
    /// <returns>The fresh renderer state bound to the selected catalogs.</returns>
    public RendererState CreateSession() => new(_catalogs);

    /// <summary>Creates a processor for messages sent by the renderer with the selected catalogs.</summary>
    /// <param name="functions">Host functions associated with the selected catalogs.</param>
    /// <param name="onAction">Optional callback for validated actions.</param>
    /// <param name="onError">Optional callback for validated renderer errors.</param>
    /// <param name="onRendererFunctionResponse">Optional callback for validated renderer function responses.</param>
    /// <returns>The inbound message processor.</returns>
    public RendererMessageProcessor CreateMessageProcessor(AgentFunctions? functions = null,
        RendererMessageHandler? onAction = null, RendererMessageHandler? onError = null,
        RendererMessageHandler? onRendererFunctionResponse = null) =>
        new(_catalogs, functions, onAction, onError, onRendererFunctionResponse);

    /// <summary>Negotiates a request against renderer capabilities.</summary>
    /// <param name="registered">The configured catalog registry.</param>
    /// <param name="capabilities">The required renderer capabilities.</param>
    /// <param name="acceptInline">Whether the host accepts validated inline catalogs.</param>
    /// <param name="processor">The resulting request configuration.</param>
    /// <param name="examples">Ordered example batches, validated against the selected catalogs.</param>
    /// <returns>A diagnostic when negotiation or example validation fails.</returns>
    public static A2uiDiagnostic? Negotiate(CatalogRegistry registered, JsonElement capabilities, bool acceptInline,
        out RequestProcessor? processor, IEnumerable<string>? examples = null)
    {
        processor = null;
        if (capabilities.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return new("CatalogError", "", "Renderer capabilities are required for negotiation.");
        return Create(registered, capabilities, acceptInline, examples, out processor);
    }

    /// <summary>Creates a fixed-format configuration using all registered catalogs.</summary>
    /// <param name="registered">The configured catalog registry.</param>
    /// <param name="processor">The resulting request configuration.</param>
    /// <param name="examples">Ordered example batches.</param>
    /// <returns>A diagnostic when catalog or example validation fails.</returns>
    public static A2uiDiagnostic? Fixed(CatalogRegistry registered, out RequestProcessor? processor,
        IEnumerable<string>? examples = null) => Create(registered, default, false, examples, out processor);

    private static A2uiDiagnostic? Create(CatalogRegistry registered, JsonElement capabilities, bool acceptInline,
        IEnumerable<string>? examples, out RequestProcessor? processor)
    {
        processor = null;
        if (!CatalogNegotiation.TryResolve(registered, capabilities, acceptInline, out var active, out var diagnostic))
            return diagnostic;
        if (DirectJsonPrompt.Create(active!, out var prompt, examples) is { } error)
            return error;
        processor = new(active!, prompt!);
        return null;
    }
}
