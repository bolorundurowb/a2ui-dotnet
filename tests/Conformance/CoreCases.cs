using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2UI.Core;

internal static class CoreCases
{
    internal static string? CatalogExclusion(string file, JsonElement test, string id)
    {
        if (file == "conformance/core/multi_catalog.yaml")
            return id switch
            {
                "test_multi_catalog_function_call_catalog_id_override" => null,
                "test_multi_catalog_version_mismatch_error" => null,
                "test_multi_catalog_format_string_index_and_catalog_call_without_default" =>
                    "The fixture routes components using bare catalogId. The pinned wire protocol requires @catalogId.",
                _ => "The fixture expects abstract selection from a per-surface supportedCatalogIds list. Core resolves concrete items from the session registry."
            };

        if (file == "conformance/core/message_processor_v1_0.yaml")
            return id switch
            {
                "test_v10_legacy_catalog_id_routes_by_default_catalog_schema" or
                "test_v10_legacy_catalog_id_routes_when_target_declares_non_string_catalog_id" or
                "test_v10_function_call_without_catalog_id_uses_surface_default" or
                "test_v10_calls_in_lists_naming_their_catalog" or
                "test_v10_index_function_on_surface_without_default" or
                "test_v10_nested_call_in_args_on_surface_without_default_errors" or
                "test_v10_call_in_index_args_on_surface_without_default_errors" =>
                    "The fixture routes components using bare catalogId. The pinned wire protocol requires @catalogId.",
                "test_v10_call_to_undeclared_function_without_strict_mode_accepted" =>
                    "Core validates function declarations even when progressive graph validation is enabled.",
                "test_v10_call_naming_catalog_of_other_protocol_version_errors" =>
                    "The fixture registers a legacy catalog. The registry rejects it at registration.",
                _ => null
            };

        if (file == "conformance/core/catalog.yaml" && id == "test_v09_catalog_derive_keeps_protocol_version")
            return "Legacy protocol is outside the profile.";
        if (file != "conformance/core/catalog.yaml" || !test.GetProperty("action").ValueEquals("from_json"))
            return null;

        return id switch
        {
            "test_v09_catalog_from_json_user_activation_agent_only_loads" or
            "test_v09_catalog_from_json_allows_hyphen_identifiers" => "Legacy protocol is outside the profile.",
            "test_v10_uax31_invalid_argument_name" =>
                "The fixture uses a parameters map. The pinned schema declares arguments under properties/args.",
            "test_v10_catalog_from_json_leaf_defs" =>
                "The fixture uses metadata.displayName, which the pinned catalog schema does not allow.",
            "test_catalog_from_json_override_catalog_id" => "The registry uses the document identity and has no catalog ID override.",
            "test_v10_catalog_from_json_filters_entries_by_unions" => "The registry retains declared entries. Union-based transformation belongs to #67.",
            "test_v10_catalog_from_json_v1_0_attributes" or
            "test_v10_catalog_from_json_user_activation_renderer_only" or
            "test_v10_catalog_from_json_uax31_validation" or
            "test_v10_uax31_valid_non_ascii_identifier" =>
                "The fixture omits required 1.0 function schema fields.",
            "test_v10_published_basic_catalog_validates_nested_calls" =>
                "The fixture allows calls to unregistered catalogs during standalone validation. Core validates calls against the registry.",
            _ => null
        };
    }

    internal static bool Supports(string file, string id, JsonElement test) => (file, id) switch
    {
        ("conformance/core/expressions.yaml" or "conformance/core/composition_constraints.yaml", _) => true,
        ("conformance/core/index_function.yaml", "test_index_function_outside_loop_error") => true,
        ("conformance/core/message_processor_v1_0.yaml", "test_global_recursion_depth_limit_exceeded_error") => true,
        ("conformance/core/validator_v1_0.yaml", "test_validator_1_0" or "test_custom_catalog_1_0" or
            "test_v10_recursion_limit_function_call_depth_exceeded_error" or
            "test_v10_recursion_limit_function_call_depth_at_limit" or
            "test_v10_recursion_limit_function_call_depth_one_past_limit_error" or
            "test_v10_data_model_nesting_depth_limit_exceeded_error" or
            "test_v10_agent_only_function_in_binding_error" or "test_v10_agent_only_function_in_action_error" or
            "test_v10_agent_only_function_cross_catalog_and_nested_error") => true,
        ("conformance/core/catalog.yaml", _) when test.GetProperty("action").ValueEquals("from_json") => true,
        ("conformance/core/multi_catalog.yaml", "test_multi_catalog_function_call_catalog_id_override" or
            "test_multi_catalog_version_mismatch_error") => true,
        ("conformance/core/message_processor_v1_0.yaml",
            "test_v10_get_renderer_capabilities" or "test_v10_get_renderer_capabilities_inline_catalogs" or
            "test_v10_function_call_naming_catalog_without_that_function_errors" or
            "test_v10_function_call_naming_catalog_with_invalid_args_errors" or
            "test_v10_function_call_naming_catalog_with_valid_args" or
            "test_v10_strict_function_call_to_schema_only_function_of_surface_catalog" or
            "test_v10_function_call_naming_unknown_catalog_errors" or "test_v10_function_call_with_empty_catalog_id_errors" or
            "test_v10_function_call_without_catalog_id_uses_surface_default" or
            "test_v10_function_call_without_catalog_id_to_unknown_function_errors" or
            "test_v10_function_call_without_catalog_id_on_surface_without_default_errors" or
            "test_v10_index_function_on_surface_with_default" or
            "test_v10_nested_call_in_args_resolves_its_own_catalog" or "test_v10_nested_call_in_args_naming_its_catalog" or
            "test_v10_call_in_index_args_is_checked" or
            "test_v10_call_in_list_without_catalog_id_uses_surface_default" or
            "test_v10_call_in_list_with_invalid_args_errors" or "test_v10_undefined_system_function_errors" or
            "test_v10_function_call_with_empty_name_errors" or
            "test_v10_call_without_catalog_id_on_surface_without_default_errors_without_strict_mode" or
            "test_v10_call_naming_unknown_catalog_errors_without_strict_mode") => true,
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
            "test_v10_get_renderer_data_model_includes_surface_without_default" or
            "test_v10_get_renderer_data_model_excludes_surface_without_default_from_v09" or
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
            "test_v10_component_metadata_catalog_round_trip" or "test_v10_mcp_catalog_round_trip" or
            "test_v10_custom_catalog_round_trip_function_metadata" or "test_v10_custom_catalog_round_trip_leaf_defs" or
            "test_v10_basic_validation_schema" or "test_v10_custom_validation_schema_leaf_defs") => true,
        ("conformance/core/catalog.yaml", _) when test.GetProperty("action").ValueEquals("derive") => true,
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
            case "conformance/core/multi_catalog.yaml":
                RunMixedCatalog(test, assets);
                break;
            case "conformance/core/message_processor_v1_0.yaml"
                when test.GetProperty("action").ValueEquals("get_renderer_capabilities"):
                var registry = new CatalogRegistry(includeBasicCatalog: false);
                foreach (var definition in test.GetProperty("catalogs").EnumerateArray())
                    RegisterCatalog(registry, JsonNode.Parse(definition.GetRawText())!.AsObject());
                var capabilities = registry.CreateRendererCapabilities(test.GetProperty("args").GetProperty("includeInlineCatalogs").GetBoolean());
                Require(Equal(capabilities.Value, test.GetProperty("expect")), "Renderer capabilities differ.");
                break;
            case "conformance/core/message_processor_v1_0.yaml":
            case "conformance/core/validator_v1_0.yaml":
            case "conformance/core/composition_constraints.yaml":
            case "conformance/core/index_function.yaml":
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
                if (definition == "IndexSystemFunction" && decoded!.Kind == "updateComponents")
                {
                    // This probe tests schema shape. The graph suite checks collection scope.
                    diagnostic = registry.ValidateComponent(decoded.Payload.GetProperty("components")[0], "probe");
                    break;
                }
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
            if (definition != "IndexSystemFunction")
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

    private static void RunMixedCatalog(JsonElement test, string assets)
    {
        if (test.GetProperty("action").ValueEquals("select_catalog"))
        {
            var mixedRegistry = new CatalogRegistry(includeBasicCatalog: false);
            foreach (var catalog in test.GetProperty("args").GetProperty("catalogs").EnumerateObject())
            {
                var definition = JsonNode.Parse(catalog.Value.GetRawText())!.AsObject();
                definition["catalogId"] = catalog.Name;
                var registered = mixedRegistry.TryRegister(Encoding.UTF8.GetBytes(definition.ToJsonString()), out var diagnostic);
                var supported = catalog.Value.GetProperty("protocolVersion").ValueEquals(WireMessage.Version);
                Require(supported ? registered : !registered && diagnostic?.Code == "unsupportedVersion",
                    diagnostic?.ToString() ?? "Catalog version outcome differs.");
            }
            return;
        }

        var registry = BuildRegistry(test, assets, repairMixedCatalogReference: true);
        using var session = new SurfaceSession(registry);
        foreach (var step in test.GetProperty("steps").EnumerateArray())
            RunStep(session, step, validate: true);

        foreach (var surface in test.GetProperty("expect").GetProperty("surfaces").EnumerateObject())
        {
            using var resolver = new NodeResolver(session, registry, surface.Name);
            foreach (var component in surface.Value.GetProperty("components").EnumerateObject())
            {
                var node = resolver.Nodes.Single(item => item.ComponentId == component.Name);
                foreach (var property in component.Value.EnumerateObject())
                    Require(property.NameEquals("component")
                            ? property.Value.ValueEquals(node.ComponentType)
                            : node.Props.TryGetValue(property.Name, out var actual) && actual.Value is { } value &&
                              Equal(value, property.Value),
                        $"Resolved property {property.Name} differs.");
            }
        }
    }

    private static void RunCatalog(JsonElement test, string assets)
    {
        if (test.GetProperty("action").ValueEquals("from_json"))
        {
            var customRegistry = new CatalogRegistry(includeBasicCatalog: false);
            var fromFile = test.TryGetProperty("catalogPath", out var catalogPath);
            var definitionBytes = fromFile ? File.ReadAllBytes(Path.Combine(assets, catalogPath.GetString()!))
                : CatalogFixtureBytes(test);
            using var inputDocument = JsonDocument.Parse(definitionBytes);
            var input = inputDocument.RootElement;
            var registered = customRegistry.TryRegister(definitionBytes, out var diagnostic);
            if (test.TryGetProperty("expectError", out _))
            {
                Require(!registered, "Expected catalog registration to fail.");
                var id = test.GetProperty("name").GetString()!;
                if (id == "test_catalog_from_json_missing_catalog_id_error")
                    Require(diagnostic?.Code == "invalidCatalogId", diagnostic?.ToString() ?? "Expected a missing catalog ID diagnostic.");
                if (id == "test_v10_catalog_from_json_component_metadata_invalid_uax31_key_error")
                    Require(diagnostic?.Path.Contains("/metadata/extensions", StringComparison.Ordinal) == true,
                        diagnostic?.ToString() ?? "Expected an extension name diagnostic.");
                if (id.Contains("user_activation", StringComparison.Ordinal))
                    Require(diagnostic?.Path.EndsWith("/requiresUserActivation", StringComparison.Ordinal) == true,
                        diagnostic?.ToString() ?? "Expected an activation diagnostic.");
                if (test.GetProperty("expectError").TryGetProperty("message", out var message) &&
                    message.ValueEquals("Unsupported external $ref"))
                    Require(diagnostic?.Code == "unresolvedReference", diagnostic?.ToString() ?? "Expected an offline reference diagnostic.");
                if ((id.Contains("uax31", StringComparison.Ordinal) && !fromFile) || id.Contains("prefixed_function", StringComparison.Ordinal))
                    Require(diagnostic?.Code == "invalidCatalogItem", diagnostic?.ToString() ?? "Expected an identifier diagnostic.");
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
                foreach (var (field, valid) in new[] { ("validComponents", true), ("invalidComponents", false) })
                    if (expected.TryGetProperty(field, out var fixtures))
                        foreach (var component in fixtures.EnumerateArray())
                        {
                            var rejection = customRegistry.ValidateComponent(component, input.GetProperty("catalogId").GetString());
                            Require((rejection is null) == valid, rejection?.ToString() ?? "Expected a component rejection.");
                        }
                if (expected.TryGetProperty("matchesBuiltinCatalog", out var matches) && matches.GetBoolean())
                    Require(new CatalogRegistry().TryGetDefinition(CatalogRegistry.BasicCatalogId, out var basic) &&
                        Equal(definition, basic), "Published Basic Catalog differs from the built-in definition.");
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
        switch (test.GetProperty("action").GetString())
        {
            case "derive":
                RunDerive(test, assets);
                return;
            case "validation_schema":
                RunValidationSchema(test, assets);
                return;
        }

        var roundTrip = test.GetProperty("action").GetString() == "round_trip";
        var bytes = roundTrip
            ? Encoding.UTF8.GetBytes(LoadCatalogFixture(test, assets).ToJsonString())
            : File.ReadAllBytes(Path.Combine(assets, test.GetProperty("expectFile").GetString()!));
        var registry = new CatalogRegistry(includeBasicCatalog: !roundTrip);
        if (roundTrip)
            Require(registry.TryRegister(bytes, out var error), error?.ToString() ?? "Registration failed.");
        using var published = JsonDocument.Parse(bytes);
        var catalogId = published.RootElement.GetProperty("catalogId").GetString()!;
        Require(registry.TryGetDefinition(catalogId, out var catalog), "Missing catalog.");
        Require(Equal(catalog, published.RootElement), "Published catalog differs.");
        if (!roundTrip)
            return;

        // Serializing the loaded definition and loading it again must not change it.
        var second = new CatalogRegistry(includeBasicCatalog: false);
        Require(second.TryRegister(Encoding.UTF8.GetBytes(catalog.GetRawText()), out var secondError),
            secondError?.ToString() ?? "Second registration failed.");
        Require(second.TryGetDefinition(catalogId, out var again) && Equal(again, published.RootElement),
            "The definition changed after a second round trip.");
    }

    // The upstream fixtures below disagree with the pinned 1.0 catalog schema. The adapter repairs each difference in
    // memory, leaves the pinned files untouched and keeps the behaviour the case names. docs/schema-validation.md
    // records every repair.
    private static JsonObject LoadCatalogFixture(JsonElement test, string assets)
    {
        if (test.TryGetProperty("catalogPath", out var path))
            return JsonNode.Parse(File.ReadAllBytes(Path.Combine(assets, path.GetString()!)))!.AsObject();

        var catalog = JsonNode.Parse(test.GetProperty("catalog").GetRawText())!.AsObject();
        catalog["protocolVersion"] ??= WireMessage.Version;
        // The pinned schema allows only `extensions` in catalog metadata, and extension keys must be identifiers.
        if (catalog["metadata"] is JsonObject metadata)
        {
            metadata.Remove("displayName");
            if (metadata["extensions"] is JsonObject extensions)
                metadata["extensions"] = new JsonObject(extensions.Select(member =>
                    KeyValuePair.Create(member.Key.Replace('-', '_'), member.Value?.DeepClone())));
        }
        // The pinned schema requires anyFunction beside anyComponent.
        if (catalog["$defs"] is JsonObject definitions)
            definitions["anyFunction"] ??= new JsonObject { ["not"] = new JsonObject() };
        return catalog;
    }

    private static void RunValidationSchema(JsonElement test, string assets)
    {
        var registry = new CatalogRegistry(includeBasicCatalog: false);
        var catalog = LoadCatalogFixture(test, assets);
        Require(registry.TryRegister(Encoding.UTF8.GetBytes(catalog.ToJsonString()), out var error),
            error?.ToString() ?? "Registration failed.");
        Require(registry.TryCreateValidationSchema(catalog["catalogId"]!.GetValue<string>(), out var schema),
            "The catalog has no validation schema.");
        var golden = JsonNode.Parse(File.ReadAllBytes(Path.Combine(assets, test.GetProperty("expectFile").GetString()!)))!;
        var actual = JsonNode.Parse(schema.GetRawText())!;
        Require(JsonNode.DeepEquals(Normalize(golden), Normalize(actual)), "The validation schema differs from the golden schema.");
    }

    // Upstream comparisons ignore the order of enum values and required names.
    private static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject members:
                foreach (var member in members.ToArray())
                {
                    var value = Normalize(member.Value);
                    if (member.Key is "enum" or "required" && value is JsonArray list)
                        members[member.Key] = new JsonArray(list.OrderBy(item => item?.ToJsonString(), StringComparer.Ordinal)
                            .Select(item => item?.DeepClone()).ToArray());
                }
                return members;
            case JsonArray items:
                foreach (var item in items)
                    Normalize(item);
                return items;
            default:
                return node;
        }
    }

    // The upstream derive source declares functions through the legacy call/returnType shape and a theme, and the
    // pinned 1.0 catalog schema accepts neither. The adapter writes the 1.0 function shape and drops the theme, so
    // the case cannot check that derive keeps the theme.
    private static void RunDerive(JsonElement test, string assets)
    {
        var source = LoadCatalogFixture(test, assets);
        source.Remove("theme");
        foreach (var function in source["functions"]!.AsObject())
            AdaptFunction(function.Value!.AsObject());

        var registry = new CatalogRegistry(includeBasicCatalog: false);
        Require(registry.TryRegister(Encoding.UTF8.GetBytes(source.ToJsonString()), out var error),
            error?.ToString() ?? "Source registration failed.");
        var sourceId = source["catalogId"]!.GetValue<string>();

        var args = test.GetProperty("args");
        var derivedId = args.GetProperty("catalogId").GetString()!;
        var derivation = new CatalogDerivation
        {
            Components = ReadEntries(args, "components", adapt: false),
            Functions = ReadEntries(args, "functions", adapt: true),
            Instructions = args.TryGetProperty("instructions", out var instructions) ? instructions.GetString() : null
        };
        var derived = registry.TryDerive(sourceId, derivedId, derivation, out var diagnostic);

        if (test.TryGetProperty("expectError", out var expectedError))
        {
            Require(!derived && diagnostic?.Code == "unknownCatalogItem" &&
                expectedError.GetProperty("category").ValueEquals("CatalogError") &&
                diagnostic.Message.Contains(expectedError.GetProperty("message").GetString()!, StringComparison.Ordinal),
                diagnostic?.ToString() ?? "Expected derivation to fail.");
            Require(!registry.CatalogIds.Contains(derivedId), "A failed derivation registered a catalog.");
            return;
        }

        Require(derived, diagnostic?.ToString() ?? "Derivation failed.");
        Require(registry.TryGetDefinition(derivedId, out var definition), "Missing derived catalog.");
        Require(registry.TryGetDefinition(sourceId, out var unchanged) && Equal(unchanged,
            ParseElement(source.ToJsonString())), "Derivation changed the source catalog.");

        foreach (var property in test.GetProperty("expect").EnumerateObject())
        {
            switch (property.Name)
            {
                case "catalogId" or "protocolVersion" or "instructions":
                    Require(definition.GetProperty(property.Name).ValueEquals(property.Value.GetString()!),
                        $"Derived {property.Name} differs.");
                    break;
                case "components" or "functions" when property.Value.ValueKind == JsonValueKind.Array:
                    Require(definition.GetProperty(property.Name).EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal)
                        .SequenceEqual(property.Value.EnumerateArray().Select(name => name.GetString()!).Order(StringComparer.Ordinal)),
                        $"Derived {property.Name} differ.");
                    break;
                case "components":
                    var entries = definition.GetProperty("components");
                    Require(entries.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal)
                        .SequenceEqual(property.Value.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal)),
                        "Derived components differ.");
                    foreach (var entry in property.Value.EnumerateObject())
                        Require(Equal(entries.GetProperty(entry.Name), entry.Value), $"Derived component {entry.Name} differs.");
                    break;
                case "absentKeys":
                    foreach (var key in property.Value.EnumerateArray())
                        Require(!definition.TryGetProperty(key.GetString()!, out _), $"Derived catalog keeps {key.GetString()}.");
                    break;
                case "theme":
                    break;
                default:
                    throw new InvalidDataException($"Unknown derive expectation {property.Name}.");
            }
        }
    }

    // A string keeps the source's entry. A one-member object defines an entry.
    private static List<CatalogEntry>? ReadEntries(JsonElement args, string group, bool adapt)
    {
        if (!args.TryGetProperty(group, out var items))
            return null;
        var entries = new List<CatalogEntry>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                entries.Add(CatalogEntry.Keep(item.GetString()!));
                continue;
            }

            var definition = item.EnumerateObject().Single();
            var schema = JsonNode.Parse(definition.Value.GetRawText())!.AsObject();
            if (adapt)
                AdaptFunction(schema);
            entries.Add(CatalogEntry.Define(definition.Name, ParseElement(schema.ToJsonString())));
        }
        return entries;
    }

    private static void AdaptFunction(JsonObject function)
    {
        var properties = function["properties"]!.AsObject();
        properties["@call"] = properties["call"]!.DeepClone();
        properties.Remove("call");
        function["returnType"] = properties["returnType"]!["const"]!.DeepClone();
        properties.Remove("returnType");
        function["required"] = new JsonArray("@call");
    }

    private static byte[] CatalogFixtureBytes(JsonElement test)
    {
        var definition = JsonNode.Parse(test.GetProperty("catalog").GetRawText())!.AsObject();
        definition["protocolVersion"] ??= WireMessage.Version;
        if (test.TryGetProperty("catalogId", out var id))
            definition["catalogId"] ??= id.GetString();
        return Encoding.UTF8.GetBytes(definition.ToJsonString());
    }

    private static void RunLifecycle(JsonElement test, string assets)
    {
        var isComposition = test.GetProperty("name").GetString()!.StartsWith("test_composition_", StringComparison.Ordinal);
        var registry = BuildRegistry(test, assets, compositionFixture: isComposition);
        var strict = test.TryGetProperty("strictMode", out var strictMode) && strictMode.GetBoolean();
        var validate = test.TryGetProperty("action", out var action) && action.ValueEquals("validate");
        var options = strict ? GraphValidationOptions.Strict : GraphValidationOptions.Progressive;
        if (test.TryGetProperty("validationConfig", out var config))
            options = new()
            {
                AllowMissingRoot = IsAllowed("allowMissingRoot"),
                AllowDanglingReferences = IsAllowed("allowDanglingReferences"),
                AllowOrphanComponents = IsAllowed("allowOrphanComponents")
            };
        using var session = new SurfaceSession(registry, options);
        bool IsAllowed(string flag) => config.TryGetProperty(flag, out var enabled) && enabled.GetBoolean();
        if (test.TryGetProperty("steps", out var steps))
        {
            var lastStep = steps.GetArrayLength() - 1;
            var index = 0;
            foreach (var step in steps.EnumerateArray())
                RunStep(session, step, validate, index++ == lastStep && test.TryGetProperty("expectError", out var error)
                    ? error : default);
            CheckState(session, test);
        }
        else
            RunStep(session, test, validate);
    }

    // The registry holds the pinned Basic Catalog under the aliases the fixtures use, plus any catalogs the case declares.
    internal static CatalogRegistry BuildRegistry(JsonElement test, string assets, bool repairMixedCatalogReference = false, bool compositionFixture = false)
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

        if (test.TryGetProperty("catalog", out var inline))
        {
            if (inline.TryGetProperty("catalogSchema", out var nested))
                inline = nested;
            if (inline.TryGetProperty("catalogId", out _))
            {
                var definition = JsonNode.Parse(inline.GetRawText())!.AsObject();
                if (compositionFixture)
                    SupplyCompositionSchema(definition);
                RegisterCatalog(registry, definition);
            }
        }
        if (test.GetProperty("name").ValueEquals("test_v10_agent_only_function_cross_catalog_and_nested_error"))
            RegisterCatalog(registry, JsonNode.Parse(File.ReadAllBytes(Path.Combine(assets,
                "conformance/test_data/catalogs/allowed_callers_catalog_v1_0.json")))!.AsObject());
        if (test.TryGetProperty("catalogs", out var definitions) && definitions.ValueKind == JsonValueKind.Array)
            foreach (var definition in definitions.EnumerateArray())
                if (definition.TryGetProperty("catalogId", out _))
                {
                    var authored = JsonNode.Parse(definition.GetRawText())!.AsObject();
                    if (repairMixedCatalogReference)
                    {
                        // This fixture references a local DynamicString that it does not define.
                        authored["components"]!["Text"]!["properties"]!["text"]!["$ref"] =
                            "common_types.json#/$defs/DynamicString";
                    }
                    RegisterCatalog(registry, authored);
                }
        return registry;
    }

    private static void SupplyCompositionSchema(JsonObject definition)
    {
        // Upstream's fixture adapter supplies the component discriminator and ChildList definition.
        foreach (var (name, schema) in definition["components"]!.AsObject())
        {
            schema!["type"] ??= "object";
            schema["properties"] ??= new JsonObject();
            schema["properties"]!["component"] = new JsonObject { ["const"] = name };
            if (schema["properties"]!["children"] is JsonObject children &&
                children["$ref"]?.GetValue<string>() == "#/$defs/ChildList")
                children["$ref"] = "common_types.json#/$defs/ChildList";
        }
    }

    private static void RegisterCatalog(CatalogRegistry registry, JsonObject definition)
    {
        definition["protocolVersion"] ??= WireMessage.Version;
        Require(registry.TryRegister(Encoding.UTF8.GetBytes(definition.ToJsonString()), out var error),
            error?.ToString() ?? "Catalog registration failed.");
    }

    private static void RunStep(SurfaceSession session, JsonElement step, bool validate, JsonElement error = default)
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

        if (step.TryGetProperty("expectError", out var stepError))
            error = stepError;
        if (error.ValueKind != JsonValueKind.Undefined)
        {
            var category = diagnostic?.Code switch
            {
                null => null,
                "graphDepth" or "functionCallDepth" or "dataModelDepth" or "componentValueDepth" when validate => "RecursionError",
                "unallowedParent" or "unallowedChild" => "ValidationError",
                "duplicateComponent" or "circularReference" or "missingRoot" or "orphanComponent" or "danglingReference" or
                    "unallowedParent" or "unallowedChild" or "graphDepth" when validate => "ValidationError",
                "unknownSurface" or "duplicateSurface" or "duplicateComponent" or "circularReference" or "missingRoot" or
                    "orphanComponent" or "danglingReference" or "unallowedParent" or "unallowedChild" or "graphDepth" => "IntegrityError",
                "invalidDataPath" => "DataError",
                "unknownCatalog" => "CatalogError",
                "unsupportedMessage" => null,
                _ => "ValidationError"
            };
            if (error.ValueKind == JsonValueKind.String)
                Require(diagnostic?.Message.Contains(error.GetString()!, StringComparison.OrdinalIgnoreCase) == true,
                    diagnostic?.ToString() ?? "Expected rejection.");
            else
            {
                var expectedCategory = error.GetProperty("category").GetString();
                // The pinned validator fixtures use both categories for recursion failures.
                Require(category == expectedCategory || (category == "RecursionError" && expectedCategory == "ValidationError"),
                    diagnostic?.ToString() ?? "Expected rejection.");
                if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                    Require(RendererError.FromDiagnostic(diagnostic!).Code == code.GetString(), diagnostic!.ToString());
                if (diagnostic?.Code is "unallowedChild" or "unallowedParent" && error.TryGetProperty("path", out var path))
                    Require(RendererError.FromDiagnostic(diagnostic).Path == path.GetString(), diagnostic.ToString());
                if (error.TryGetProperty("message", out var expected) && expected.GetString() is { } expectedText &&
                    (expectedText is "Global recursion limit exceeded" or "functionCall depth" ||
                     expectedText.StartsWith("@index function", StringComparison.Ordinal) ||
                     expectedText.Contains("Allowed parents:", StringComparison.Ordinal) ||
                     expectedText.Contains("Allowed children:", StringComparison.Ordinal) ||
                     expectedText.Contains("is not reachable from", StringComparison.Ordinal)))
                    Require(diagnostic?.Message.Contains(expectedText, StringComparison.OrdinalIgnoreCase) == true,
                        diagnostic?.ToString() ?? "Expected rejection.");
                if (error.TryGetProperty("rendererError", out var renderer) &&
                    renderer.GetProperty("error") is var wireError &&
                    wireError.TryGetProperty("message", out var rendererMessage) && rendererMessage.ValueEquals("agentOnly"))
                {
                    Require(diagnostic?.Code == "agentOnlyFunction", diagnostic?.ToString() ?? "Expected rejection.");
                    var mapped = RendererError.FromDiagnostic(diagnostic!);
                    Require(mapped.Path == wireError.GetProperty("path").GetString() &&
                        mapped.Message.Contains("agentOnly", StringComparison.Ordinal), mapped.Message);
                }
            }
            return;
        }

        Require(diagnostic is null, diagnostic?.ToString() ?? "");
        CheckState(session, step);
    }

    private static void CheckState(SurfaceSession session, JsonElement step)
    {
        if (step.TryGetProperty("action", out var action) && action.ValueEquals("get_renderer_data_model"))
        {
            var version = step.GetProperty("args").GetProperty("version").GetString()!;
            var model = session.GetRendererDataModel(version);
            var expected = step.GetProperty("expect");
            Require(model is null ? expected.ValueKind == JsonValueKind.Null : Equal(model.Value, expected),
                "Renderer data model metadata differs.");
            return;
        }

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
                var expectedComponents = components.ValueKind == JsonValueKind.Array
                    ? components.EnumerateArray().Select(component => (component.GetProperty("id").GetString()!, component)).ToArray()
                    : components.EnumerateObject().Select(component => (component.Name, component.Value)).ToArray();
                Require(snapshot.Components.Count == expectedComponents.Length, "Component count differs.");
                foreach (var (id, component) in expectedComponents)
                {
                    Require(snapshot.Components.TryGetValue(id, out var actual), "Missing component.");
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

    private static JsonElement ParseElement(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static bool Equal(JsonElement left, JsonElement right) =>
        JsonNode.DeepEquals(JsonNode.Parse(left.GetRawText()), JsonNode.Parse(right.GetRawText()));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
