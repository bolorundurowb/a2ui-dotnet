# A2UI 1.0 schema validation

Core uses an in-house evaluator and resolves references offline. An
application supplies its catalog registry. The SDK packages have no
third-party dependencies. The embedded A2UI protocol schemas and Basic
Catalog come from revision `d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe`
and retain their Apache-2.0 license.

## Evaluated schema profile

The evaluator handles type, const, enum, required, properties,
patternProperties, propertyNames, additionalProperties, unevaluatedProperties,
minProperties, maxProperties, items, minItems, contains, uniqueItems, minimum,
pattern, date-time format, allOf, anyOf, oneOf, not, if, then, else, and
registered document or JSON Pointer references. It returns the first
diagnostic with an instance JSON Pointer. Defaults and documentation
annotations leave the input unchanged. Keywords outside this subset are
ignored. General Draft 2020-12 validation and schema generation are outside
this implementation. Treat custom catalog schemas as application configuration
and enable inline registration only within a host trust policy.

Wire decode checks the message independently of surface state. Catalog-specific
component fields and function arguments are validated once the relevant catalog
is known. A component selects its explicit catalog before the surface default.
Nested calls select their own catalog from their explicit ID or the surface
default.

## Wire fidelity

A decoded message owns its JSON. The decoder parses the input, rejects it or
clones the root, and keeps nothing from the caller's buffer, so the buffer can
be cleared or returned to a pool as soon as decoding returns. The package smoke
check clears each input buffer before reading the message.

- **Numbers.** Number tokens keep their original text, including `1.0`,
  `1.00E+2`, `-0.0`, values beyond double precision and values beyond double
  range such as `1e400`. Core does not convert them to `double` unless a schema
  keyword needs a numeric comparison.
- **Duplicate members.** A duplicate member name at any depth rejects the whole
  value with a `duplicateProperty` diagnostic. Its path is the JSON Pointer of
  the second occurrence.
- **Absent and null.** An absent optional member stays absent and an explicit
  `null` stays `null`, in decoded messages and after encoding. The protocol
  makes `updateDataModel.value` required and uses an explicit `null` to delete
  a value, so an omitted `value` is a `missingProperty` error.
- **Unresolved.** A reference that has no target is neither absent nor null.
  An unknown catalog is `unknownCatalog`, an unregistered schema reference is
  `unresolvedReference`, and a data pointer with no value reads as not found.
  A stored `null` reads as found with a null value.
- **Diagnostics.** Each failure has a category in its `Code` and the JSON
  Pointer of the offending value, with `~` and `/` escaped in member names.

Wire decoding applies to one complete JSON value. SSE events, JSON Lines and
model chunk framing belong to the host or to `A2UI.Agent`.

## Comparison on .NET 8

The test-only comparison project uses JsonSchema.Net 7.3.4. Its resolved
net8.0 dependencies are MIT-licensed JsonPointer.Net 5.3.1, Json.More.Net 2.1.1
and Humanizer.Core 2.14.1. The locally inspected 9.6.0 package declares an
OSMFEULA license file. The 7.3.4 evaluator fails to load the pinned
`Extensions` definition because the .NET regex engine does not recognize
`XID_Start` and `XID_Continue`. The comparison removes that pattern from
an in-memory copy for a workload that contains no extensions. No upstream
asset is changed.

One Release run on Windows x64 with .NET 8.0.34 validated the Basic Catalog
Text component with a relative `DynamicString` reference 20,000 times per
validator after warm-up. Core took 25.09 µs and allocated 7,247 bytes per
operation. JsonSchema.Net 7.3.4 with diagnostic list output took 61.34 µs and
allocated 100,767 bytes. These are measurements of this fixture only.
The in-house evaluator also passed the mixed-catalog and unresolved-reference
smoke checks and ran in a fresh trimmed .NET 8 Windows x64 package consumer.

## Upstream differences

Core uses the pinned schema's `@catalogId` on components. The processor fixture
`test_v10_component_catalog_override` uses `catalogId` and supplies placeholder
catalogs without component definitions. The same spelling occurs in the
processor fixtures for explicit component routing and updates without an
override. Core preserves bare `catalogId` for catalog-defined component
properties. The validator fixture `test_v10_uax31_invalid_identifier_error`
requires identifier syntax for component IDs, while the pinned
`common_types.json#/$defs/ComponentId` specifies a string. The conformance
report records these differences at the pinned revision above.

The processor fixtures `test_v10_update_components_stores_component_metadata`,
`test_v10_update_components_replaces_component_metadata`,
`test_v10_update_components_omitted_metadata_clears_component_metadata` and
`test_v10_inline_catalog_id_and_metadata_standard_only_beside_inline_id` send
component metadata as bare `metadata`. The pinned `ComponentCommon` declares
`@metadata`, so Core reads only that member and treats a bare `metadata` as a
catalog-defined property.

The `reserved_keys` fixtures `test_escaped_doubled_at_unescaping` and
`test_escaped_doubled_at_metadata_unescaping` expect `@@path` to be stored as
`@path`. Core keeps wire values unchanged and the unescaping is left to
resolved nodes ([#12](https://github.com/bolorundurowb/a2ui-dotnet/issues/12)).

The `validate_common_type` fixtures for `Surface`, `DynamicValue` and
`IndexSystemFunction` run through a probe catalog with one component property
for the definition and a `now` function. A `FunctionCall` is checked against the
catalog's function list, so a call is only valid when the catalog declares it.
The two schema parity fixtures compare the embedded pinned documents with the
specification files. Core serves those documents instead of generating them
from SDK models.

Derived catalogs can select entries
from a registered source. The upstream `derive_source_catalog_v1_0.json`
fixture includes legacy function shapes and a theme field, which the pinned
1.0 catalog schema rejects. Derivation with replacement entries and golden
generated schema comparisons remain pending.
