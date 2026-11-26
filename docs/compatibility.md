# A2UI 1.0 stable release profile

This matrix defines the first stable release target. The current implementation verifies the Core wire codec, catalog registry and ordered surface state through package smoke tests and selected pinned upstream fixtures. Protocol coverage requires the applicable pinned suites and the two-way scripted interoperability replay. See the [upstream baseline](../upstream/README.md).

## First consumer scenario

Assume a .NET appointment-booking backend and an upstream Python Core headless test client. Use the pinned Basic Catalog for the first replay. Also exercise a small registered custom catalog and a mixed-catalog surface before claiming custom catalog coverage. The replay uses an in-process bridge and a scripted provider with synthetic data. The ASP.NET Core sample will demonstrate ordered SSE delivery and a POST return channel. Model clients, credentials, transport, durable state and business effects belong to the host.

The first turn negotiates capabilities, creates the form and sends separate component and data updates. The client edits a field, reorders a templated list and returns the submit action with resolved context and opted-in data. The next turn confirms the submission and deletes the surface. Replay both .NET Agent → upstream Core and upstream Agent → .NET Core. These are scenario assumptions pending a named integration and captured transcripts.

## Required stable coverage

| Area | Release target | Verification |
| --- | --- | --- |
| Core wire and state | A2UI 1.0 in both directions, including versioned metadata, ordered surface lifecycle, strict and progressive graph validation, JSON Pointer updates and reactive bindings, expressions and scopes. | All applicable pinned 1.0 agent-to-renderer, common type and reserved key cases pass, together with selected lifecycle and topology cases and all pinned 1.0 pointer and deletion cases. Seven pinned 1.0 node resolution cases pass, including three metadata cases adapted to the pinned `@metadata` spelling. The dispatch-metadata case uses a bare component `metadata` property. 24 node resolution cases load the v0.9.1 Basic Catalog, which the pinned manifest does not import. Nested scope, occurrence identity and scoped write-back have focused 1.0 smoke coverage. Cross-language replay is pending. |
| Core catalogs and interaction | Basic Catalog components and portable functions, offline registered custom and mixed catalogs, resolved accessibility attributes, actions, diagnostics and owner-scoped data sync. Host-bound functions use explicit registration. | Basic Catalog serialization and mixed-catalog validation smoke checks pass. The Basic Catalog functions run through `FunctionEvaluator` and `BasicFunctions` using the A2UI regular expression dialect, and reactive check rules report results on resolved nodes. The 1.0 function and validation result cases pass. Six legacy 0.9 function cases are excluded. Locale selection supports en-US only. A `NodeResolver` created with a `FunctionCallRouter` sends agent calls in properties and checks to the agent, reports a check that waits for an answer as pending, and cancels the calls of detached nodes. `ActionDispatcher` resolves event and function actions at invocation time. `RendererError` maps rejections to the wire error codes. `SurfaceSession` reports opt-in data model metadata and `CatalogRegistry` reports renderer capabilities. 22 of 24 error response cases and 13 of 18 action cases pass. The exclusions are one 0.9 case, one case that needs UAX #31 component IDs, three action cases that use 0.9 syntax and two that need local functions with effects. Host-implemented functions are registered with `FunctionEvaluator.RegisterHostFunction`. Replay is pending. |
| RPC | Agent → renderer calls and renderer → agent calls, each with replies in the opposite direction. Validate caller, activation and arguments. Cover correlation, errors, cancellation and reactive sharing. | `AgentFunctionClient` correlates calls to the agent, with a configurable per-client pending call limit and duplicate, no listener, timeout, cancellation, disposal and late reply handling. The pinned catalogue schema reserves user activation for renderer-only functions. `RendererFunctionHandler` answers calls from the agent. `FunctionCallRouter` sends calls the renderer cannot run to the agent, and shares, caches and cancels them as the protocol describes. 30 of 33 `rpc_functions` cases and 41 of 43 `agent_function_routing` cases pass. The action fixtures verify delivery of the correlated result and preservation of earlier host effects when a remote step fails. Template fixtures verify partial text with an expression error and local-only resolution in the selected catalogue. Excluded are two cases whose catalogue the pinned schema rejects, two legacy 0.9 cases and one case with a NaN argument. The component binding and action cases, and the pending check routing case, also run through mounted nodes. For those runs the harness declares the remote functions without an implementation, because Core rejects calls to undeclared functions, and renames bare `checks` to `@checks`, as the pinned schema requires. The Agent renderer RPC suite passes 29 of 30 cases. Cross-language replay is pending. |
| Agent runtime | Capability and catalog negotiation, fixed and negotiated catalogs, policy-gated inline catalogs, pruning, prompts, examples and skills. Validate deterministic and generated messages through Core. Route actions, diagnostics and RPC across turns. | Pending pinned Agent cases and replay. |
| Direct JSON | Batch compile, decompile and response parsing, plus incremental streaming with ordered text and UI, partial-result diagnostics, cancellation and size limits. | The pinned 1.0 compile, decompile, response parsing and streaming cases pass, including all 15 mixed-catalog batch cases and 57 streaming cases. The 82 v0.8 and v0.9 streaming cases are excluded as legacy. Smoke tests cover cancellation, the size limit and failure after a partial send. The four streaming growth and overhead cases fail the upstream limits. See [#73](https://github.com/bolorundurowb/a2ui-dotnet/issues/73). |
| Express | Batch compile, decompile, wrapping, response parsing and prompt generation with the Basic Catalog golden corpus. | Pending pinned Agent cases and scaling checks. |
| Express streaming | Incremental parsing and emission, ordered text and UI, and diagnostics for partial or malformed output. | Pending [#33](https://github.com/bolorundurowb/a2ui-dotnet/issues/33) and the pinned Express streaming cases. |
| Agent macros | Catalog-aware expansion with scoped IDs, nested macros and shared Core validation. | `MacroExpander` passes 45 of the 46 pinned macro cases that use a 1.0 envelope. The 24 v0.9 cases and one case that sends `"@catalogId": null` are excluded. See [Agent macros](#agent-macros). |
| Trimming and Native AOT | Preserve support in the Core and Agent API design. Verify headless consumers for each claimed deployment target. | Windows x64 scaffold publish and execution passed for trimmed .NET 8 and Native AOT .NET 8 and 10. A trimmed .NET 8 Core package consumer and a .NET 8 Native AOT smoke application passed with the wire, catalog and surface APIs. Broader deployment verification is pending. |

## Core catalog verification

The Release run against upstream revision `d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe` passed 510 Core and 251 Agent cases on .NET 8.0.34 for Windows x64. It recorded 355 Core and 779 Agent exclusions, with no failures. The Core run includes all ten composition cases, the additional validator and recursion cases, and the out-of-scope `@index` check. The Agent total includes all four skill export cases in `conformance/agent/skill.yaml`.

| Coverage | Passed | Excluded |
| --- | ---: | ---: |
| A2UI 1.0 catalog loading, including metadata | 25 | 9 |
| Mixed catalog fixtures | 2 | 6 |
| Message processor catalog and function routing group | 19 | 9 |
| Renderer capabilities, with and without inline catalogs | 2 | 0 |
| Catalog derivation, validation schemas and round trips | 14 | 9 |

The published 1.0 Basic, MCP and VegaChart definitions load with their references resolved offline against the document and the embedded shared types. `CatalogRegistry.TryDerive` and `TryCreateValidationSchema` cover the derivation, bundled schema and round-trip cases for 1.0, including the Basic and custom validation goldens. Their 14 passes are the seven derive cases, two validation schema cases and five round-trip cases, and the nine exclusions are the legacy v0.8 and v0.9 cases. The harness adapts fixtures that the pinned schema rejects, as [schema validation](schema-validation.md#upstream-differences) records. The conformance report records exclusions for legacy routing, abstract per-surface catalog selection, and fixtures that disagree with the pinned schema. For example, the function override fixture in `multi_catalog.yaml` references an undefined local `#/$defs/DynamicString`. The harness supplies the pinned `common_types.json#/$defs/DynamicString` reference and checks the resolved value through `NodeResolver`.

The Core smoke check covers a Unicode component and function argument schema, rejection of an invalid argument name, a renderer function that requires user activation, and explicit component routing on a mixed catalog surface.

## Agent inbound messages

Use `AgentFunctions.Register` to add host function declarations to the agent's copy of a catalogue before negotiation. Use `RequestProcessor.CreateMessageProcessor` to bind those handlers to the selected catalogues, then pass renderer envelopes to `ProcessAsync`. Action, error and renderer-function response callbacks receive validated messages. A missing callback or a callback that declines leaves the message available to the host. An agent function call produces a correlated `agentFunctionResponse` with a value or an RPC error.

The renderer enforces any catalogue-declared user activation requirement before sending an agent function call. The host authenticates the renderer, isolates conversations and owns model retries, business side effects and storage.

All 64 `agent_functions.yaml` cases pass against pinned revision `d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe`, including argument defaults, catalogue selection, callback routing and validation of model output. The complete Windows x64 Release run on .NET 8.0.34 passed 510 Core and 315 Agent cases with 355 Core and 715 Agent exclusions and no failures. The clean Agent package smoke check opens a form, routes a returned action, serves an agent function and closes the form in the second turn.

The pinned agent-functions catalogue fixture has no components and omits `$defs.anyComponent`. When populating a catalogue's schema unions, registration supplies a false union for an empty component set. The fixture's function declarations are kept intact. [Pinned catalogue fixture](https://github.com/a2ui-project/a2ui/blob/d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe/conformance/test_data/catalogs/agent_functions_catalog_v1_0.json)

## Mixed-catalog Direct JSON

On a surface without a default catalog, the batch compiler and response parser resolve each unqualified component and component function by its name across the active catalogs. Shared names need an explicit override. Compiled messages identify the component catalog with `@catalogId` and the function catalog with `catalogId`. A surface that explicitly declares a default retains that default, including when an unqualified nested RPC call uses it.

The pinned inference fixture `test_direct_json_compile_same_component_name_in_two_catalogs` uses bare `catalogId` on `Badge` instances. A2UI 1.0 wire routing uses `@catalogId`. The inference parser accepts the fixture spelling on mixed surfaces and emits the wire spelling. The harness compares against the corresponding wire field. The RPC fixtures keep an explicitly supplied surface default and reject an unqualified nested call on a surface without one. [Pinned mixed-catalog Direct JSON fixtures](https://github.com/a2ui-project/a2ui/blob/d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe/conformance/agent/multi_catalog_formats.yaml)

All 15 Direct JSON cases in the pinned mixed-catalog suite pass. The Windows x64 Release run on .NET 8 passed 510 Core and 360 Agent cases with 355 Core and 670 Agent exclusions and no failures.

## Agent calls to renderer functions

Create one `RendererFunctionClient` per conversation with `RequestProcessor.CreateRendererFunctionClient` to validate calls against the negotiated catalogues. Send the returned invocation's `Message` through the application transport and await its `Result`. Route replies from `CreateMessageProcessor`'s `onRendererFunctionResponse` callback to `TryHandleResponse`. Dispose the client at the end of the conversation to fail outstanding calls. Calls expire after 30 seconds and the client allows up to 256 pending calls by default. Both defaults are configurable. If supplying a call identifier, use a fresh one so that a late reply cannot be mistaken for the answer to a new call.

The Windows x64 Release run on .NET 8.0.34 passed 510 Core and 344 Agent cases, with 355 Core and 686 Agent exclusions and no failures. It includes 29 of 30 cases in `renderer_function_calls.yaml`. The remaining case constructs a `rendererOrAgent` function requiring user activation, a combination rejected during registration by the pinned A2UI 1.0 catalogue schema. [Pinned renderer function cases](https://github.com/a2ui-project/a2ui/blob/d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe/conformance/agent/renderer_function_calls.yaml) · [Pinned catalogue schema](https://github.com/a2ui-project/a2ui/blob/d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe/specification/v1_0/json/catalog_definition.json)

## Core state behaviour

`SurfaceSession` defaults to progressive topology validation for streamed updates. It accepts temporarily missing roots, forward references and orphans. Use `GraphValidationOptions.Strict` to require a complete reachable graph after each nonempty component batch. Both modes enforce composition, cycles and a 50-edge graph nesting limit before committing. Core also checks JSON container nesting in component values and data models, limits nested component function calls to five, and rejects renderer bindings or actions that call agent-only functions. A component with an explicit `@index` call may arrive before its parent in a progressive update. When the component is reachable from the root, every occurrence of it must be inside a collection template. A rejected operation preserves the previous snapshot and emits no change event.

Hosts serialise calls into each session. Local `WriteData` calls and remote `updateDataModel` messages commit in call order, with the last successful write taking effect. Reads use immutable snapshots. A missing pointer returns false, and a stored JSON null returns true with a null value. A null write deletes an object member, leaves a null slot in an array, or resets the root to an empty object. Change events carry the committed snapshot and the write's pointer so consumers can refresh bindings that overlap it. Reentrant writes are rejected.

Progressive component updates share unchanged entries between snapshots. For batches of leaves, the session checks each changed component and its incoming references, including references to components that are still arriving. Structural changes and strict sessions use the full candidate graph validator.

Both pinned A2UI 1.0 progressive Core scaling workloads pass the 8× limit on Windows x64. The harness measures the UTF-8 codec and session together in Release mode. The Basic Catalog workload compares 200 and 800 updates, and the composition workload compares 1,000 and 4,000. [Pinned scaling fixtures](https://github.com/a2ui-project/a2ui/blob/d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe/conformance/core/message_processor_scaling.yaml) · [Core scaling investigation](https://github.com/bolorundurowb/a2ui-dotnet/issues/56)

The same .NET 8.0.34 scaling run passed the two Direct JSON compile cases. The Agent streaming growth ratios were 15.86×, 16.43× and 11.78× against a limit of 8×, and the streaming overhead was 647.05× against a limit of 10×. All four failures remain tracked in [#73](https://github.com/bolorundurowb/a2ui-dotnet/issues/73).

## Core resolved nodes

`NodeResolver` exposes component metadata and resolved accessibility attributes on `ResolvedNode`, with separate change events for each. Hidden accessibility attributes propagate to descendants. After a surface is deleted, its resolver releases its subscriptions. Create a new resolver if a later surface uses the same ID.

Nested template items retain identity through insertion, reordering and removal by matching their JSON values, in list order for duplicates. For unmatched values, an unmatched occurrence at the same index survives an in-place edit. Moved nodes receive updated positions and scopes in the same notification batch. Hosts should treat occurrence IDs as opaque. Core continues to store the original wire values and unescapes doubled `@@` keys when resolving component properties.

On .NET 8.0.34 for Windows x64, the Release conformance run passed 543 Core and 405 Agent cases, with 322 Core and 625 Agent exclusions and no failures. The pinned revision is `d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe`.

| Pinned suite | Passed | Exclusions and adaptations |
| --- | ---: | --- |
| `data_context.yaml` | 11 | The shared scope cases exercise the 1.0 snapshot API. |
| `node_resolution.yaml` | 7 | Three metadata cases use an in-memory translation of bare `metadata` to the pinned wire spelling `@metadata`. 24 cases require the legacy Basic Catalog and one dispatch-metadata case uses a bare envelope key. |
| `reserved_keys.yaml` | 10 | Both doubled-`@@` evaluation cases assert resolved properties through `NodeResolver`. |
| `accessibility.yaml` | 5 | The adapter checks explicit attributes. Implied names, platform roles and interactive states, and axe-core checks belong to a renderer adapter. The transitional bare `accessibility` case disagrees with the pinned schema and is excluded. |

[Core blueprint](https://github.com/a2ui-project/a2ui/blob/d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe/blueprints/modules/a2ui_core.blueprint.md) · [Pinned Core fixtures](https://github.com/a2ui-project/a2ui/tree/d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe/conformance/core)

## Agent macros

`MacroExpander` replaces each macro component in ordered 1.0 agent messages with the catalog components it stands for. Other components and messages keep their order. The root of an expansion keeps the ID of the call, and generated components are named `<call ID>__<name>_<n>`, children before parents. Each component uses the catalog from its `@catalogId`, then the catalog of the macro that emitted it, then the surface default. On a surface whose default catalog the application did not register, components that name no catalog are left alone. Expansion stops with `RecursionError` beyond `MaxDepth` (32 by default), and with `ExpansionLimitError` beyond `MaxComponents` (10,000). A macro call with unsuitable arguments stays in the output by default, or stops the expansion when `ErrorMode` is `Raise`. `CreateCatalog` and `AugmentCatalog` write catalogs that a model can use to call the macros. Validate the expansion with `RendererState.Prepare` or a `SurfaceSession`, as for any other agent messages.

On .NET 8.0.34 for Windows x64, the Release conformance run against `d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe` passed 531 Core and 405 Agent cases, with 334 Core and 625 Agent exclusions and no failures. It is 45 more Agent passes than before the macro work. The suite in `conformance/agent/macros/macros.yaml` has 70 cases.

| Group | Cases | Result |
| --- | ---: | --- |
| 1.0 envelope, including the `v1_0` twin cases | 45 | Pass |
| `36_surface_lifecycle_delete_recreate_and_null_catalog_id` | 1 | Excluded. The pinned message schema requires `@catalogId` to be a string, so Core rejects the null before the expander sees it. |
| v0.9 envelope, which is the suite default | 24 | Excluded as outside the profile |

The harness reads `envelope_version` for each case. A case that names only a `version` or an `expander_protocol_version` uses that value, and one that names none is v0.9. These adaptations are needed to run the pinned fixtures:

- Custom catalogs refer to `common_types.json` by absolute URL. The harness changes the reference to the relative form that Core resolves offline.
- The suite describes two behaviours only as comments beside the template. `StatusBadge` and `AnnotatedBadge` upper-case `status`, and `PositiveCountCard` rejects a negative count. The harness adds both.
- The templates use the heading variants `h1` to `h3`. The pinned 1.0 Basic Catalog only has `caption` and `body`, and the 1.0 goldens use `body`, so the harness uses `body`.
- `org.a2ui.basic` is registered as another name of the Basic Catalog, because `08_surface_lifecycle_envelopes` uses it.
- `allow_missing_root` also allows orphan components, since nothing can be reachable from a root that does not exist.

Focused regression checks in the package smoke test cover ID namespacing and order, the depth and size limits, passthrough and raise modes, the reserved `Surface` name and the default macro catalog rewrite. They also check that the generated catalogs register in Core and that the expansion passes `RendererState.Prepare`. The trimmed .NET 8 and Native AOT .NET 8 deployment smoke runs include them and produced no analyser warnings. [Pinned macro cases](https://github.com/a2ui-project/a2ui/blob/d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe/conformance/agent/macros/macros.yaml)

## Optional integrations

| Capability | Planned scope |
| --- | --- |
| Optional catalog extensions | Verify declared extension catalogs and their host requirements, starting with MCP and VegaChart v1 as candidates. [#35](https://github.com/bolorundurowb/a2ui-dotnet/issues/35) |
| Renderer framework adapters | Ship separately from the SDKs. Select and verify the first adapter, with Blazor as a candidate. [#36](https://github.com/bolorundurowb/a2ui-dotnet/issues/36) |
| Additional transports | Ship independently of the SDKs. Select and verify the first binding beyond the ASP.NET Core SSE sample, with A2A and AG-UI as candidates. [#37](https://github.com/bolorundurowb/a2ui-dotnet/issues/37) |

A2UI 0.9.1 and other legacy wire versions remain outside the stable profile. Reconsider 0.9.1 with a named consumer and captured payloads in the required directions.

## .NET package compatibility

Both SDK packages target `net8.0` and have no third-party library dependencies. `A2UI.Agent` depends on `A2UI.Core`, and the package version `0.1.0-dev` is independent of the wire version. Public NuGet returned HTTP 404 for the candidate IDs `A2UI.Core` and `A2UI.Agent` on 22 November 2026. Confirm ownership and availability before publication.

Release build and local package smoke verification ran on Windows x64 with SDK 11.0.100. The checks run the Core wire, catalog and session APIs from both packages in clean consumers with roll-forward disabled. A new trimmed .NET 8 Windows x64 Core package consumer and a rooted Native AOT smoke application passed.

| Runtime | Package smoke result | Microsoft servicing as of 22 November 2026 |
| --- | --- | --- |
| .NET 8 | Passed on 8.0.34 | Ended 10 November 2026 |
| .NET 9 | Passed on 9.0.23 | Ended 10 November 2026 |
| .NET 10 | Passed on 10.0.15 | Active LTS |
| .NET 11 | Passed on 11.0.0 | Active STS |

Microsoft tracks servicing independently of package compatibility. [Official .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
