using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

internal static class CoreCases
{
    internal static bool Supports(string file, string id) => (file, id) switch
    {
        ("conformance/core/expressions.yaml", _) when id.StartsWith("test_expr_", StringComparison.Ordinal) => true,
        ("conformance/core/data_model_pointers.yaml" or "conformance/core/data_deletion.yaml", _) => true,
        ("conformance/core/validator_v1_0.yaml", _) when id.StartsWith("test_v10_topology_", StringComparison.Ordinal) ||
            id.StartsWith("test_v10_component_nesting_depth_", StringComparison.Ordinal) ||
            id.StartsWith("test_v10_incremental_update_", StringComparison.Ordinal) => true,
        ("conformance/core/validator_v1_0.yaml", "test_v10_update_data_model_invalid_path_error") => true,
        ("conformance/core/message_processor_v1_0.yaml",
            "test_v10_create_surface_inline_initialization" or "test_v10_create_surface_optional_catalog_id" or
            "test_v10_create_surface_with_metadata_extensions" or "test_v10_create_surface_metadata_extension_key_must_be_identifier" or
            "test_v10_create_surface_metadata_only_holds_extensions" or "test_multiple_update_types_error" or
            "test_batch_message_array_and_wrapper" or "test_v10_rejects_v08_begin_rendering_action" or
            "test_v10_non_string_surface_id_error" or "test_missing_version_field_in_v10_process_messages_error" or
            "test_batch_duplicate_component_ids_in_same_message_error" or "test_update_components_on_deleted_surface_error" or
            "test_update_data_model_on_deleted_surface_error" or "test_delete_non_existent_surface_is_safe_noop" or
            "test_batch_atomic_rollback_on_candidate_topology_cycle" or "test_v10_update_data_model_deletion" or
            "test_batch_multi_stage_lifecycle_pipeline" or "test_permissive_mode_allows_orphan_components" or
            "test_permissive_mode_allows_dangling_references" or "test_v10_permissive_incremental_streaming_out_of_order_before_root" or
            "test_composition_constraints_preserved_on_partial_parent_update") => true,
        ("conformance/core/common_types.yaml",
            "test_v10_function_response_has_exactly_one_outcome" or "test_v10_function_response_rejects_null_error") => true,
        ("conformance/core/catalog.yaml", "test_v10_basic_catalog_to_json" or "test_v10_basic_catalog_round_trip" or
            "test_v10_catalog_from_json_rejects_ref_to_another_catalog" or
            "test_v10_catalog_from_json_rejects_absolute_uri_ref" or "test_v10_catalog_from_json_rejects_external_ref_in_function_args" or
            "test_v10_catalog_from_json_accepts_local_and_common_types_refs") => true,
        _ => false
    };

    internal static void Run(string file, JsonElement test, string assets)
    {
        switch (file)
        {
            case "conformance/core/expressions.yaml":
                var parsed = ExpressionTemplate.TryParse(test.GetProperty("input").GetString()!, out var parts, out var parseError);
                if (test.TryGetProperty("expect_error", out var expectedError))
                {
                    Require(!parsed && parseError is not null && parseError.Code == expectedError.GetProperty("category").GetString() &&
                        parseError.Message.Contains(expectedError.GetProperty("message").GetString()!, StringComparison.Ordinal),
                        parseError?.ToString() ?? "Expected parser rejection.");
                }
                else
                    Require(parsed && Equal(test.GetProperty("expect"), parts), parseError?.ToString() ?? "Parsed parts differ.");
                break;
            case "conformance/core/message_processor_v1_0.yaml":
            case "conformance/core/validator_v1_0.yaml":
            case "conformance/core/data_model_pointers.yaml":
            case "conformance/core/data_deletion.yaml":
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
        if (test.GetProperty("action").ValueEquals("from_json"))
        {
            var customRegistry = new CatalogRegistry();
            var input = test.GetProperty("catalog");
            var registered = customRegistry.TryRegister(Encoding.UTF8.GetBytes(input.GetRawText()), out var diagnostic);
            if (test.TryGetProperty("expectError", out _))
            {
                Require(!registered && diagnostic?.Code == "unresolvedReference",
                    diagnostic?.ToString() ?? "Expected offline reference rejection.");
                return;
            }
            Require(registered, diagnostic?.ToString() ?? "Catalog registration failed.");
            Require(customRegistry.TryGetDefinition(input.GetProperty("catalogId").GetString()!, out var definition) &&
                Equal(input, definition), "Catalog definition differs.");
            if (test.TryGetProperty("expect", out var expected))
                foreach (var property in expected.EnumerateObject())
                    if (property.Name is "catalogId" or "protocolVersion" or "components" or "functions")
                        Require(definition.TryGetProperty(property.Name, out var actual) && (property.Value.ValueKind == JsonValueKind.Array
                                ? actual.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal)
                                    .SequenceEqual(property.Value.EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal))
                                : Equal(property.Value, actual)),
                            $"Catalog field {property.Name} differs.");
            return;
        }
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
        // Some pinned fixtures refer to the Basic Catalog using test aliases or its previous URI.
        foreach (var alias in new[] { "test-catalog-v10", "basic", "https://a2ui.org/catalogs/basic/v1/catalog.json" })
        {
            catalog["catalogId"] = alias;
            catalog["$id"] = alias;
            Require(registry.TryRegister(Encoding.UTF8.GetBytes(catalog.ToJsonString()), out var error),
                error?.ToString() ?? "Catalog registration failed.");
        }

        if (test.TryGetProperty("catalog", out var inline) && inline.TryGetProperty("catalogId", out _))
            RegisterCatalog(registry, JsonNode.Parse(inline.GetRawText())!.AsObject());
        if (test.TryGetProperty("catalogs", out var definitions) && definitions.ValueKind == JsonValueKind.Array)
            foreach (var definition in definitions.EnumerateArray())
                if (definition.TryGetProperty("catalogId", out _))
                    RegisterCatalog(registry, JsonNode.Parse(definition.GetRawText())!.AsObject());

        var strict = test.TryGetProperty("strictMode", out var strictMode) && strictMode.GetBoolean();
        var validate = test.TryGetProperty("action", out var action) && action.ValueEquals("validate");
        using var session = new SurfaceSession(registry, strict ? GraphValidationOptions.Strict : GraphValidationOptions.Progressive);
        if (test.TryGetProperty("steps", out var steps))
        {
            foreach (var step in steps.EnumerateArray())
                RunStep(session, step, validate);
            CheckSurfaces(session, test);
        }
        else
            RunStep(session, test, validate);
    }

    private static void RegisterCatalog(CatalogRegistry registry, JsonObject definition)
    {
        definition["protocolVersion"] ??= WireMessage.Version;
        Require(registry.TryRegister(Encoding.UTF8.GetBytes(definition.ToJsonString()), out var error),
            error?.ToString() ?? "Catalog registration failed.");
    }

    private static void RunStep(SurfaceSession session, JsonElement step, bool validate)
    {
        var messages = step.TryGetProperty("messages", out var batch) ? batch : step.GetProperty("payload");
        if (messages.ValueKind == JsonValueKind.Object)
            messages = messages.GetProperty("messages");

        A2uiDiagnostic? diagnostic = null;
        foreach (var item in messages.EnumerateArray())
        {
            if (!WireMessage.TryDecode(Encoding.UTF8.GetBytes(item.GetRawText()), WireDirection.AgentToRenderer,
                    out var message, out diagnostic))
                break;
            var id = message!.Payload.GetProperty("surfaceId").GetString()!;
            session.TryGetSurface(id, out var before);
            var notifications = 0;
            void Observe(SurfaceChange _) => notifications++;
            session.Changed += Observe;
            try
            {
                diagnostic = session.Apply(message);
            }
            finally
            {
                session.Changed -= Observe;
            }
            if (diagnostic is not null)
            {
                session.TryGetSurface(id, out var after);
                Require(ReferenceEquals(before, after) && notifications == 0, "A rejected operation changed committed state.");
                break;
            }
        }

        if (step.TryGetProperty("expectError", out var error))
        {
            var category = diagnostic?.Code switch
            {
                null => null,
                "graphDepth" when validate => "RecursionError",
                "duplicateComponent" or "circularReference" or "missingRoot" or "orphanComponent" or "danglingReference" or
                    "invalidComposition" or "graphDepth" when validate => "ValidationError",
                "unknownSurface" or "duplicateSurface" or "duplicateComponent" or "circularReference" or "missingRoot" or
                    "orphanComponent" or "danglingReference" or "invalidComposition" or "graphDepth" => "IntegrityError",
                "invalidDataPath" => "DataError",
                "unsupportedMessage" => null,
                _ => "ValidationError"
            };
            if (error.ValueKind == JsonValueKind.String)
                Require(diagnostic?.Message.Contains(error.GetString()!, StringComparison.OrdinalIgnoreCase) == true,
                    diagnostic?.ToString() ?? "Expected rejection.");
            else
            {
                var expectedCategory = error.GetProperty("category").GetString();
                // The pinned validator suite labels the same graph depth limit with two categories.
                Require(category == expectedCategory || (diagnostic?.Code == "graphDepth" && validate &&
                    expectedCategory == "ValidationError" && error.TryGetProperty("message", out var expectedMessage) &&
                    expectedMessage.ValueEquals("Global recursion limit exceeded")), diagnostic?.ToString() ?? "Expected rejection.");
            }
            return;
        }

        Require(diagnostic is null, diagnostic?.ToString() ?? "");
        CheckSurfaces(session, step);
    }

    private static void CheckSurfaces(SurfaceSession session, JsonElement step)
    {
        if (!step.TryGetProperty("expect", out var expect) || !expect.TryGetProperty("surfaces", out var surfaces))
            return;
        foreach (var surface in surfaces.EnumerateObject())
        {
            var exists = session.TryGetSurface(surface.Name, out var snapshot);
            Require(exists == (!surface.Value.TryGetProperty("exists", out var expectedExists) || expectedExists.GetBoolean()),
                "Surface existence differs.");
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
