internal static class ProfileCases
{
    private const string Legacy = "Legacy protocol is outside the profile.";
    private const string RequestProcessor = "conformance/agent/request_processor.yaml";
    private const string PromptGenerator = "conformance/agent/express/prompt_generator.yaml";

    internal static (string Status, string Reason)? Classify(string path, string id) => (path, id) switch
    {
        ("conformance/core/message_processor_v0_8.yaml" or "conformance/core/message_processor_v0_9.yaml" or
            "conformance/core/validator_v0_9.yaml", _) => ("excluded", Legacy),
        ("conformance/agent/renderer_capabilities.yaml", _) when id.StartsWith("v0.", StringComparison.Ordinal) ||
            id == "both keys present returns only v0.9 key for v0.9" => ("excluded", Legacy),
        (RequestProcessor, "test_direct_json_create_format_accepts_several_v0_9_catalogs" or
            "test_express_create_format_rejects_several_v0_9_catalogs") => ("excluded", Legacy),
        (PromptGenerator, "test_express_snippet_omits_event_user_message_before_v1_0" or
            "test_express_snippet_omits_system_functions_before_v1_0") => ("excluded", Legacy),
        ("conformance/agent/direct_json/scaling.yaml", _) when id.EndsWith("_v09", StringComparison.Ordinal) =>
            ("excluded", Legacy),
        ("conformance/agent/atom/compiler.yaml" or "conformance/agent/elemental/compiler.yaml", _) =>
            ("excluded", "This experimental Python-only inference format is outside the stable profile."),
        ("conformance/agent/builder/builder.yaml", _) when id.EndsWith("_v1_0", StringComparison.Ordinal) =>
            ("excluded", "The optional typesafe builder is outside the first stable profile (#74)."),
        ("conformance/agent/streamed_answer.yaml", _) =>
            ("excluded", "The optional agent-renderer streamed answer is outside the first stable profile (#75)."),
        ("conformance/core/message_processor_v1_0.yaml", _) when id.StartsWith("test_v10_call_validation_config_", StringComparison.Ordinal) =>
            ("excluded", "The optional per-call validation configuration is outside the first stable profile (#76)."),
        ("conformance/agent/express/response_streaming.yaml", _) =>
            ("pending", "Incremental Express response streaming is tracked in #33."),
        (PromptGenerator, _) => ("pending", "Express prompt generation is tracked in #71."),
        (RequestProcessor, "test_format_override_replaces_the_generator_format") =>
            ("pending", "Express format override in the request processor is tracked in #71."),
        (RequestProcessor, _) when id.Contains("express", StringComparison.OrdinalIgnoreCase) =>
            ("pending", "Express format binding in the request processor is tracked in #71."),
        ("conformance/agent/skill.yaml", _) when id.StartsWith("test_express_", StringComparison.Ordinal) =>
            ("pending", "Express skill generation is tracked in #71."),
        ("conformance/core/actions.yaml", "test_action_function_call_local_mutation_no_event_dispatched" or
            "test_action_function_call_execution_error") =>
            ("pending", "The conformance adapter for registered host effects is tracked in #25."),
        _ => null
    };
}
