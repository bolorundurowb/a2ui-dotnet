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
default. JSON number tokens keep their original precision and representation.
Optional absent members remain absent, explicit JSON null remains null, and
duplicate members are rejected.

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
catalogs without component definitions. Derived catalogs can select entries
from a registered source. The upstream `derive_source_catalog_v1_0.json`
fixture includes legacy function shapes and a theme field, which the pinned
1.0 catalog schema rejects. Derivation with replacement entries and golden
generated schema comparisons remain pending.
