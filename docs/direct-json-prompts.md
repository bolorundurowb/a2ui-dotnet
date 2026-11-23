# Direct JSON prompts

Generate a prompt with DirectJsonPrompt.Create using the same request-local CatalogRegistry used to validate responses. Supply examples as ordered unwrapped JSON message batches. Invalid examples return a diagnostic. The optional message allowlist filters the advertised envelopes and applies to the examples. The catalog sections preserve function caller restrictions and descriptions. ToSkill returns a monolithic Markdown skill with the prompt as its body.

At revision d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe, all 11 public builder/golden/v1_0 JSON examples compiled and were accepted as prompt examples with the Basic Catalog. On .NET 8 the Basic prompt was 43,982 UTF-8 bytes without examples. Adding the pinned VegaChart catalog increased it to 45,103 bytes. These counts measure acceptance on the fixed corpus. Provider response quality and tokenizer-specific context sizes require a model evaluation.
