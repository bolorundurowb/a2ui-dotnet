using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

internal static class CoreCases
{
    internal static bool Supports(string file, string id) => (file, id) switch
    {
        ("conformance/core/expressions.yaml", _) => true,
        ("conformance/core/data_model_pointers.yaml" or "conformance/core/data_deletion.yaml", _) => true,
        ("conformance/core/validator_v1_0.yaml", _) when id.StartsWith("test_v10_topology_", StringComparison.Ordinal) ||
            id.StartsWith("test_v10_component_nesting_depth_", StringComparison.Ordinal) ||
            id.StartsWith("test_v10_incremental_update_", StringComparison.Ordinal) => true,
        ("conformance/core/validator_v1_0.yaml",
            "test_v10_update_data_model_invalid_path_error" or "test_v10_multiple_action_envelope_error" or
            "test_v10_invalid_json_pointer_path_error" or
            "test_v10_unknown_component_type" or "test_v10_unknown_component_property" or
            "test_v10_unknown_nested_function" or "test_v10_invalid_binding_path_error" or
            "test_v10_multi_surface_independent_roots" or "test_v10_multi_surface_missing_root_error" or
            "test_v10_child_list_template_missing_path_error" or "test_v10_function_argument_schema_mismatch_error" or
            "test_v10_uax31_astral_plane_identifier_accepted" or "test_v10_uax31_astral_non_identifier_component_type_error" or
            "test_v10_validate_component_metadata_invalid_extension_key_error") => true,
        ("conformance/core/message_processor_v1_0.yaml",
            "test_unsupported_protocol_version_error" or "test_v10_component_without_catalog_id_on_surface_without_default_errors" or
            "test_v10_component_without_catalog_id_errors_with_a_single_catalog" or
            "test_v10_update_components_metadata_extension_key_must_be_identifier" or
            "test_v10_update_components_metadata_extension_key_must_be_identifier_on_update" or
            "test_v10_create_surface_theme_rejected_strict" or
            "test_v10_update_components_rejects_null_at_catalog_id" or
            "test_v10_at_catalog_id_routes_when_target_declares_optional_custom_catalog_id" or
            "test_v10_update_components_stores_at_prefixed_component_metadata" or
            "test_v10_at_prefixed_envelope_coexists_with_custom_component_properties" or
            "test_v10_at_metadata_with_omitted_optional_custom_metadata_property") => true,
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
        ("conformance/core/agent_to_renderer.yaml", "test_v10_agent_to_renderer_schema_matches_specification") => true,
        ("conformance/core/common_types.yaml", "test_v10_common_types_schema_matches_specification") => true,
        ("conformance/core/reserved_keys.yaml", _) when id is not
            ("test_escaped_doubled_at_unescaping" or "test_escaped_doubled_at_metadata_unescaping") => true,
        ("conformance/core/validator_v1_0.yaml",
            "test_v10_validate_at_prefixed_component_envelope_mixins" or
            "test_v10_validate_component_envelope_conflict_at_and_bare_error" or
            "test_v10_validate_at_checks_on_non_checkable_component_error") => true,
        ("conformance/core/message_processor_v1_0.yaml",
            "test_v10_component_with_non_string_catalog_id_errors" or "test_v10_call_with_non_string_catalog_id_errors" or
            "test_v10_update_components_rejects_null_legacy_catalog_id" or
            "test_v10_custom_envelope_properties_declared_in_combiner_branches") => true,
        ("conformance/core/common_types.yaml",
            "test_v10_index_system_function_requires_call" or "test_v10_index_system_function_rejects_null_args" or
            "test_v10_surface_component_is_surface" or "test_v10_surface_rejects_explicit_null" or
            "test_v10_dynamic_value_object_branches_are_exclusive") => true,
        ("conformance/core/common_types.yaml",
            "test_v10_function_response_has_exactly_one_outcome" or "test_v10_function_response_rejects_null_error" or
            "test_v10_extensions_keys_are_unicode_identifiers" or "test_v10_component_metadata_only_holds_extensions" or
            "test_v10_component_common_rejects_null_catalog_id" or "test_v10_function_call_rejects_unknown_keys") => true,
        ("conformance/core/catalog.yaml", "test_v10_basic_catalog_to_json" or "test_v10_basic_catalog_round_trip" or
            "test_v10_catalog_from_json_rejects_ref_to_another_catalog" or
            "test_v10_catalog_from_json_rejects_absolute_uri_ref" or "test_v10_catalog_from_json_rejects_external_ref_in_function_args" or
            "test_v10_catalog_from_json_accepts_local_and_common_types_refs") => true,
        ("conformance/core/catalog.yaml", "test_v10_catalog_from_json_component_metadata" or
            "test_v10_catalog_from_json_component_metadata_invalid_uax31_key_error" or
            "test_v10_component_metadata_catalog_round_trip") => true,
        _ => false
    };

    internal static void Run(string file, JsonElement test, string assets)
    {
        switch (file)
        {
            case "conformance/core/expressions.yaml":
                if (test.GetProperty("action").ValueEquals("validate"))
                {
                    RunSurfaceExpression(test);
                    break;
                }
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
            case "conformance/core/reserved_keys.yaml":
                RunLifecycle(test, assets);
                break;
            case "conformance/core/catalog.yaml":
                RunCatalog(test, assets);
                break;
            case "conformance/core/agent_to_renderer.yaml":
                RunSchemaParity(test, assets);
                break;
            case "conformance/core/common_types.yaml":
                if (test.GetProperty("action").ValueEquals("common_types_schema"))
                    RunSchemaParity(test, assets);
                else
                    RunCommonTypes(test);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(file));
        }
    }

    private static void RunSurfaceExpression(JsonElement test)
    {
        var registry = new CatalogRegistry();
        Require(registry.TryGetDefinition(CatalogRegistry.BasicCatalogId, out var basic), "Basic Catalog is unavailable.");
        var alias = JsonNode.Parse(basic.GetRawText())!.AsObject();
        alias["catalogId"] = "basic";
        alias["$id"] = "basic";
        Require(registry.TryRegister(Encoding.UTF8.GetBytes(alias.ToJsonString()), out var registrationError),
            registrationError?.ToString() ?? "Basic Catalog alias is unavailable.");
        using var session = new SurfaceSession(registry);
        var evaluator = new FunctionEvaluator(registry);
        foreach (var step in test.GetProperty("steps").EnumerateArray())
        {
            foreach (var item in step.GetProperty("payload").EnumerateArray())
            {
                Require(WireMessage.TryDecode(Encoding.UTF8.GetBytes(item.GetRawText()), WireDirection.AgentToRenderer,
                    out var message, out var decodeError), decodeError?.ToString() ?? "Invalid expression fixture message.");
                var applyError = session.Apply(message!);
                Require(applyError is null, applyError?.ToString() ?? "Failed to apply expression fixture message.");
            }
            Require(session.TryGetSurface("main", out var surface) && surface is not null, "Missing expression surface.");
            var call = surface!.Components["root"].GetProperty("text");
            var evaluated = evaluator.TryEvaluate(surface, "/", call, userInitiated: false, out var result, out var diagnostic);
            if (step.TryGetProperty("expectError", out var error))
                Require(!evaluated && diagnostic?.Message.Contains(error.GetProperty("message").GetString()!,
                    StringComparison.Ordinal) == true, diagnostic?.ToString() ?? "Expected evaluation failure.");
            else
            {
                var expected = test.GetProperty("expect").GetProperty("surfaces").GetProperty("main")
                    .GetProperty("components").GetProperty("root").GetProperty("text").GetString();
                Require(evaluated && result.GetString() == expected, diagnostic?.ToString() ?? "Interpolated text differs.");
            }
        }
    }

    // Core serves the pinned schema documents instead of deriving them from SDK models, so parity means the
    // public entry points return the specification files without change.
    private static void RunSchemaParity(JsonElement test, string assets)
    {
        var schema = test.GetProperty("action").GetString() switch
        {
            "agent_to_renderer_schema" => ProtocolSchemas.AgentToRenderer,
            "common_types_schema" => ProtocolSchemas.CommonTypes,
            var action => throw new InvalidDataException($"Unknown schema action {action}.")
        };
        using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(assets, test.GetProperty("expectFile").GetString()!)));
        Require(Equal(schema, expected.RootElement), "The schema differs from the specification file.");
    }

    // These definitions are not reachable from an envelope on their own. The probe catalog gives each one a
    // component property, so the check runs through the public wire decoder and surface session. A function
    // call is checked against the catalog's function list, so the catalog declares the `now` call the fixture uses.
    private static void RunCommonTypeProbe(JsonElement test)
    {
        var definition = test.GetProperty("definition").GetString()!;
        foreach (var step in test.GetProperty("steps").EnumerateArray())
        {
            var registry = new CatalogRegistry();
            RegisterCatalog(registry, new JsonObject
            {
                ["catalogId"] = "probe",
                ["functions"] = new JsonObject
                {
                    ["now"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["returnType"] = "string",
                        ["properties"] = new JsonObject { ["@call"] = new JsonObject { ["const"] = "now" } },
                        ["required"] = new JsonArray("@call")
                    }
                },
                ["components"] = new JsonObject
                {
                    ["Probe"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["value"] = new JsonObject { ["$ref"] = "common_types.json#/$defs/" + definition }
                        }
                    }
                }
            });
            using var session = new SurfaceSession(registry);
            var value = step.GetProperty("value").GetRawText();
            A2uiDiagnostic? diagnostic = null;
            foreach (var message in new[]
            {
                """{"version":"1.0","createSurface":{"surfaceId":"s","catalogId":"probe"}}""",
                $$$"""{"version":"1.0","updateComponents":{"surfaceId":"s","components":[{"id":"root","component":"Probe","value":{{{value}}}}]}}"""
            })
            {
                if (!WireMessage.TryDecode(Encoding.UTF8.GetBytes(message), WireDirection.AgentToRenderer,
                        out var decoded, out diagnostic))
                    break;
                diagnostic = session.Apply(decoded!);
                if (diagnostic is not null)
                    break;
            }

            if (step.TryGetProperty("expectError", out _))
            {
                Require(diagnostic is not null, $"Expected {definition} to reject {value}.");
                continue;
            }

            Require(diagnostic is null, $"{definition} rejected {value}: {diagnostic}");
            Require(session.TryGetSurface("s", out var surface) && surface is not null &&
                Equal(surface.Components["root"].GetProperty("value"), step.GetProperty("value")),
                $"The accepted {definition} value was not preserved.");
        }
    }

    private static void RunCommonTypes(JsonElement test)
    {
        if (test.GetProperty("definition").GetString() is "Surface" or "DynamicValue" or "IndexSystemFunction")
        {
            RunCommonTypeProbe(test);
            return;
        }

        var name = test.GetProperty("name").GetString();
        foreach (var step in test.GetProperty("steps").EnumerateArray())
        {
            var value = step.GetProperty("value").GetRawText();
            var envelope = name switch
            {
                "test_v10_extensions_keys_are_unicode_identifiers" =>
                    $$$$"""{"version":"1.0","createSurface":{"surfaceId":"s","metadata":{"extensions":{{{{value}}}}}}}""",
                "test_v10_component_metadata_only_holds_extensions" or "test_v10_component_common_rejects_null_catalog_id" =>
                    $$$$"""{"version":"1.0","updateComponents":{"surfaceId":"s","components":[{{{{value}}}}]}}""",
                "test_v10_function_call_rejects_unknown_keys" =>
                    $$$$"""{"version":"1.0","callAgentFunction":{"surfaceId":"s","functionCallId":"c","callFunction":{{{{value}}}}}}""",
                _ => $$"""{"version":"1.0","agentFunctionResponse":{{value}}}"""
            };
            var direction = name == "test_v10_function_call_rejects_unknown_keys"
                ? WireDirection.RendererToAgent : WireDirection.AgentToRenderer;
            var success = WireMessage.TryDecode(Encoding.UTF8.GetBytes(envelope), direction,
                out _, out var diagnostic);
            Require(success != step.TryGetProperty("expectError", out _), diagnostic?.ToString() ?? "Expected rejection.");
        }
    }

    private static void RunCatalog(JsonElement test, string assets)
    {
        if (test.GetProperty("action").ValueEquals("from_json"))
        {
            var customRegistry = new CatalogRegistry();
            var fromFile = test.TryGetProperty("catalogPath", out var catalogPath);
            var definitionBytes = fromFile ? File.ReadAllBytes(Path.Combine(assets, catalogPath.GetString()!))
                : Encoding.UTF8.GetBytes(test.GetProperty("catalog").GetRawText());
            using var inputDocument = JsonDocument.Parse(definitionBytes);
            var input = inputDocument.RootElement;
            var registered = customRegistry.TryRegister(definitionBytes, out var diagnostic);
            if (test.TryGetProperty("expectError", out _))
            {
                Require(!registered && (fromFile
                    ? diagnostic?.Path.Contains("/metadata/extensions", StringComparison.Ordinal) == true
                    : diagnostic?.Code == "unresolvedReference"),
                    diagnostic?.ToString() ?? "Expected catalog registration to fail.");
                return;
            }
            Require(registered, diagnostic?.ToString() ?? "Catalog registration failed.");
            Require(customRegistry.TryGetDefinition(input.GetProperty("catalogId").GetString()!, out var definition) &&
                Equal(input, definition), "Catalog definition differs.");
            if (test.TryGetProperty("expect", out var expected))
            {
                foreach (var property in expected.EnumerateObject())
                    if (property.Name is "catalogId" or "protocolVersion" or "components" or "functions")
                        Require(definition.TryGetProperty(property.Name, out var actual) && (property.Value.ValueKind == JsonValueKind.Array
                                ? actual.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal)
                                    .SequenceEqual(property.Value.EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal))
                                : Equal(property.Value, actual)),
                            $"Catalog field {property.Name} differs.");
                if (expected.TryGetProperty("componentMetadata", out var metadata))
                    foreach (var component in metadata.EnumerateObject())
                    {
                        var item = definition.GetProperty("components").GetProperty(component.Name);
                        var hasMetadata = item.TryGetProperty("metadata", out var value);
                        Require(component.Value.ValueKind == JsonValueKind.Null ? !hasMetadata : hasMetadata && Equal(component.Value, value),
                            $"Component metadata for {component.Name} differs.");
                    }
            }
            return;
        }
        var roundTrip = test.GetProperty("action").GetString() == "round_trip";
        var bytes = File.ReadAllBytes(Path.Combine(assets, test.GetProperty(roundTrip ? "catalogPath" : "expectFile").GetString()!));
        var registry = new CatalogRegistry(includeBasicCatalog: !roundTrip);
        if (roundTrip)
            Require(registry.TryRegister(bytes, out var error), error?.ToString() ?? "Registration failed.");
        using var published = JsonDocument.Parse(bytes);
        var catalogId = published.RootElement.GetProperty("catalogId").GetString()!;
        Require(registry.TryGetDefinition(catalogId, out var catalog), "Missing catalog.");
        Require(Equal(catalog, published.RootElement), "Published catalog differs.");
    }

    private static void RunLifecycle(JsonElement test, string assets)
    {
        var registry = BuildRegistry(test, assets);
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

    // The registry holds the pinned Basic Catalog under the aliases the fixtures use, plus any catalogs the case declares.
    internal static CatalogRegistry BuildRegistry(JsonElement test, string assets)
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
        return registry;
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
                    "unallowedParent" or "unallowedChild" or "graphDepth" when validate => "ValidationError",
                "unknownSurface" or "duplicateSurface" or "duplicateComponent" or "circularReference" or "missingRoot" or
                    "orphanComponent" or "danglingReference" or "unallowedParent" or "unallowedChild" or "graphDepth" => "IntegrityError",
                "invalidDataPath" => "DataError",
                "unknownCatalog" when !validate => "CatalogError",
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
                    {
                        var present = actual.TryGetProperty(property.Name, out var value);
                        // The fixture uses null to represent cleared metadata in the upstream component model.
                        var clearedMetadata = property.NameEquals("@metadata") && property.Value.ValueKind == JsonValueKind.Null;
                        Require(clearedMetadata ? !present : present && Equal(value, property.Value),
                            $"Component field {property.Name} differs.");
                    }
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
