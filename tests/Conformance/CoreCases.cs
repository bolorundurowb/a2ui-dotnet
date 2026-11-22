using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

internal static class CoreCases
{
    internal static bool Supports(string file, string id) => (file, id) switch
    {
        ("conformance/core/message_processor_v1_0.yaml",
            "test_v10_create_surface_inline_initialization" or "test_v10_create_surface_optional_catalog_id" or
            "test_v10_create_surface_with_metadata_extensions" or "test_v10_create_surface_metadata_extension_key_must_be_identifier" or
            "test_v10_create_surface_metadata_only_holds_extensions" or "test_multiple_update_types_error" or
            "test_batch_message_array_and_wrapper" or "test_v10_rejects_v08_begin_rendering_action" or
            "test_v10_non_string_surface_id_error" or "test_missing_version_field_in_v10_process_messages_error" or
            "test_batch_duplicate_component_ids_in_same_message_error" or "test_update_components_on_deleted_surface_error" or
            "test_update_data_model_on_deleted_surface_error" or "test_delete_non_existent_surface_is_safe_noop") => true,
        ("conformance/core/common_types.yaml",
            "test_v10_function_response_has_exactly_one_outcome" or "test_v10_function_response_rejects_null_error") => true,
        ("conformance/core/catalog.yaml", "test_v10_basic_catalog_to_json" or "test_v10_basic_catalog_round_trip") => true,
        _ => false
    };

    internal static void Run(string file, JsonElement test, string assets)
    {
        switch (file)
        {
            case "conformance/core/message_processor_v1_0.yaml":
                RunLifecycle(test, assets);
                break;
            case "conformance/core/catalog.yaml":
                RunCatalog(test, assets);
                break;
            case "conformance/core/common_types.yaml":
                foreach (var step in test.GetProperty("steps").EnumerateArray())
                {
                    var value = step.GetProperty("value").GetRawText();
                    var json = Encoding.UTF8.GetBytes($$"""{"version":"1.0","agentFunctionResponse":{{value}}}""");
                    var success = WireMessage.TryDecode(json, WireDirection.AgentToRenderer, out _, out var diagnostic);
                    Require(success != step.TryGetProperty("expectError", out _), diagnostic?.Code ?? "Expected rejection.");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(file));
        }
    }

    private static void RunCatalog(JsonElement test, string assets)
    {
        var roundTrip = test.GetProperty("action").GetString() == "round_trip";
        var bytes = File.ReadAllBytes(Path.Combine(assets, test.GetProperty(roundTrip ? "catalogPath" : "expectFile").GetString()!));
        var registry = new CatalogRegistry(includeBasicCatalog: !roundTrip);
        if (roundTrip)
            Require(registry.TryRegister(bytes, out var error), error?.ToString() ?? "Registration failed.");
        Require(registry.TryGetDefinition(CatalogRegistry.BasicCatalogId, out var catalog), "Missing Basic Catalog.");
        Require(Equal(catalog, JsonDocument.Parse(bytes).RootElement), "Published catalog differs.");
    }

    private static void RunLifecycle(JsonElement test, string assets)
    {
        var catalog = JsonNode.Parse(File.ReadAllBytes(Path.Combine(assets, "catalogs/basic/v1/catalog.json")))!;
        var registry = new CatalogRegistry();
        // The upstream processor fixtures refer to aliases for the Basic Catalog.
        foreach (var alias in new[] { "test-catalog-v10", "basic" })
        {
            catalog["catalogId"] = alias;
            catalog["$id"] = alias;
            Require(registry.TryRegister(Encoding.UTF8.GetBytes(catalog.ToJsonString()), out var error),
                error?.ToString() ?? "Catalog registration failed.");
        }

        using var session = new SurfaceSession(registry);
        if (test.TryGetProperty("steps", out var steps))
            foreach (var step in steps.EnumerateArray())
                RunStep(session, step);
        else
            RunStep(session, test);
    }

    private static void RunStep(SurfaceSession session, JsonElement step)
    {
        var messages = step.GetProperty("messages");
        if (messages.ValueKind == JsonValueKind.Object)
            messages = messages.GetProperty("messages");

        A2uiDiagnostic? diagnostic = null;
        foreach (var item in messages.EnumerateArray())
        {
            if (!WireMessage.TryDecode(Encoding.UTF8.GetBytes(item.GetRawText()), WireDirection.AgentToRenderer,
                    out var message, out diagnostic))
                break;
            diagnostic = session.Apply(message!);
            if (diagnostic is not null)
                break;
        }

        if (step.TryGetProperty("expectError", out var error))
        {
            var category = diagnostic?.Code switch
            {
                null => null,
                "unknownSurface" or "duplicateSurface" or "duplicateComponent" => "IntegrityError",
                "unsupportedMessage" or "unsupportedPath" => null,
                _ => "ValidationError"
            };
            Require(category == error.GetProperty("category").GetString(), diagnostic?.ToString() ?? "Expected rejection.");
            return;
        }

        Require(diagnostic is null, diagnostic?.ToString() ?? "");
        if (!step.TryGetProperty("expect", out var expect) || !expect.TryGetProperty("surfaces", out var surfaces))
            return;
        foreach (var surface in surfaces.EnumerateObject())
        {
            var exists = session.TryGetSurface(surface.Name, out var snapshot);
            Require(exists == surface.Value.GetProperty("exists").GetBoolean(), "Surface existence differs.");
            if (snapshot is null)
                continue;
            if (surface.Value.TryGetProperty("sendDataModel", out var send))
                Require(snapshot.SendDataModel == send.GetBoolean(), "sendDataModel differs.");
            if (surface.Value.TryGetProperty("dataModel", out var data))
                Require(Equal(snapshot.DataModel, data), "Data model differs.");
            if (surface.Value.TryGetProperty("components", out var components))
            {
                Require(snapshot.Components.Count == components.GetArrayLength(), "Component count differs.");
                foreach (var component in components.EnumerateArray())
                {
                    Require(snapshot.Components.TryGetValue(component.GetProperty("id").GetString()!, out var actual),
                        "Missing component.");
                    foreach (var property in component.EnumerateObject())
                        Require(actual.TryGetProperty(property.Name, out var value) && Equal(value, property.Value),
                            $"Component field {property.Name} differs.");
                }
            }
        }
    }

    private static bool Equal(JsonElement left, JsonElement right) =>
        JsonNode.DeepEquals(JsonNode.Parse(left.GetRawText()), JsonNode.Parse(right.GetRawText()));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
