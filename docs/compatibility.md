# A2UI 1.0 stable release profile

This matrix defines the first stable release target. The current implementation verifies the Core wire codec, catalog registry and ordered surface state through package smoke tests and selected pinned upstream fixtures. Protocol coverage requires the applicable pinned suites and the two-way scripted interoperability replay. See the [upstream baseline](../upstream/README.md).

## First consumer scenario

Assume a .NET appointment-booking backend and an upstream Python Core headless test client. Use the pinned Basic Catalog for the first replay. Also exercise a small registered custom catalog and a mixed-catalog surface before claiming custom catalog coverage. The replay uses an in-process bridge and a scripted provider with synthetic data. The ASP.NET Core sample will demonstrate ordered SSE delivery and a POST return channel. Model clients, credentials, transport, durable state and business effects belong to the host.

The first turn negotiates capabilities, creates the form and sends separate component and data updates. The client edits a field, reorders a templated list and returns the submit action with resolved context and opted-in data. The next turn confirms the submission and deletes the surface. Replay both .NET Agent → upstream Core and upstream Agent → .NET Core. These are scenario assumptions pending a named integration and captured transcripts.

## Required stable coverage

| Area | Release target | Verification |
| --- | --- | --- |
| Core wire and state | A2UI 1.0 in both directions, including versioned metadata, ordered surface lifecycle, strict and progressive graph validation, JSON Pointer updates and reactive bindings, expressions and scopes. | All applicable pinned 1.0 agent-to-renderer, common type and reserved key cases pass, together with selected lifecycle and topology cases and all pinned 1.0 pointer and deletion cases. Four pinned 1.0 node resolution cases pass. Metadata in node resolution is pending, and the one pinned 1.0 action invocation case cannot run because its fixture uses an undeclared `metadata` property. 24 node resolution cases load the v0.9.1 basic catalog, which the pinned manifest does not import and the 1.0 profile excludes. Expression, scope and function evaluation cases pass, except the checks on resolved nodes and `@index` outside a collection, which are pending. Cross-language replay is pending. |
| Core catalogs and interaction | Basic Catalog components and portable functions, offline registered custom and mixed catalogs, resolved accessibility attributes, actions, diagnostics and owner-scoped data sync. Host-bound functions use explicit registration. | Basic Catalog serialization and mixed-catalog validation smoke checks pass. The Basic Catalog functions run through `FunctionEvaluator` and `BasicFunctions` using the A2UI regular expression dialect, and reactive check rules report results on resolved nodes. The 1.0 function and validation result cases pass. Six legacy 0.9 function cases are excluded. Locale selection supports en-US only. Checks on resolved nodes do not yet wait for agent calls, which `FunctionCallRouter` supports for bindings and check conditions. `ActionDispatcher` resolves event and function actions at invocation time. `RendererError` maps rejections to the wire error codes. `SurfaceSession` reports opt-in data model metadata and `CatalogRegistry` reports renderer capabilities. 22 of 24 error response cases and 13 of 18 action cases pass. The exclusions are one 0.9 case, one case that needs UAX #31 component IDs, three action cases that use 0.9 syntax and two that need local functions with effects. Host-implemented functions are registered with `FunctionEvaluator.RegisterHostFunction`. Replay is pending. |
| RPC | Agent → renderer calls and renderer → agent calls, each with replies in the opposite direction. Validate caller, activation and arguments. Cover correlation, errors, cancellation and reactive sharing. | `AgentFunctionClient` correlates calls to the agent, with a configurable per-client pending call limit and duplicate, no listener, timeout, cancellation, disposal and late reply handling. The pinned catalogue schema reserves user activation for renderer-only functions. `RendererFunctionHandler` answers calls from the agent. `FunctionCallRouter` sends calls the renderer cannot run to the agent, and shares, caches and cancels them as the protocol describes. 30 of 33 `rpc_functions` cases and 41 of 43 `agent_function_routing` cases pass. The action fixtures verify delivery of the correlated result and preservation of earlier host effects when a remote step fails. Template fixtures verify partial text with an expression error and local-only resolution in the selected catalogue. Excluded are two cases whose catalogue the pinned schema rejects, two legacy 0.9 cases and one case with a NaN argument. The component binding cases run through router bindings, not mounted nodes. Pinned Agent cases and replay are pending. |
| Agent runtime | Capability and catalog negotiation, fixed and negotiated catalogs, policy-gated inline catalogs, pruning, prompts, examples and skills. Validate deterministic and generated messages through Core. Route actions, diagnostics and RPC across turns. | Pending pinned Agent cases and replay. |
| Direct JSON | Batch compile, decompile and response parsing, plus incremental streaming with ordered text and UI, partial-result diagnostics, cancellation and size limits. | The pinned 1.0 compile, decompile, response parsing and streaming cases pass, including all 57 streaming cases. The 82 v0.8 and v0.9 streaming cases are excluded as legacy. Smoke tests cover cancellation, the size limit and failure after a partial send. The four streaming growth and overhead cases fail the upstream limits. See [#56](https://github.com/bolorundurowb/a2ui-dotnet/issues/56). |
| Express | Batch compile, decompile, wrapping, response parsing and prompt generation with the Basic Catalog golden corpus. | Pending pinned Agent cases and scaling checks. |
| Express streaming | Incremental parsing and emission, ordered text and UI, and diagnostics for partial or malformed output. | Pending [#33](https://github.com/bolorundurowb/a2ui-dotnet/issues/33) and the pinned Express streaming cases. |
| Agent macros | Catalog-aware expansion with scoped IDs, nested macros and shared Core validation. | Pending [#34](https://github.com/bolorundurowb/a2ui-dotnet/issues/34) and the pinned v1.0 macro cases. |
| Trimming and Native AOT | Preserve support in the Core and Agent API design. Verify headless consumers for each claimed deployment target. | Windows x64 scaffold publish and execution passed for trimmed .NET 8 and Native AOT .NET 8 and 10. A trimmed .NET 8 Core package consumer and a .NET 8 Native AOT smoke application passed with the wire, catalog and surface APIs. Broader deployment verification is pending. |

## Core catalog verification

The Release run against upstream revision `d63fd4306da7c3ffb0c8d2b5d5ef5d4507605dbe` passed 510 Core and 251 Agent cases on .NET 8.0.34 for Windows x64. It recorded 355 Core and 779 Agent exclusions, with no failures. The Core run includes all ten composition cases, the additional validator and recursion cases, and the out-of-scope `@index` check. The Agent total includes all four skill export cases in `conformance/agent/skill.yaml`.

| Coverage | Passed | Excluded |
| --- | ---: | ---: |
| A2UI 1.0 catalog loading, including metadata | 25 | 9 |
| Mixed catalog fixtures | 2 | 6 |
| Message processor catalog and function routing group | 19 | 9 |
| Renderer capabilities, with and without inline catalogs | 2 | 0 |

The published 1.0 Basic, MCP and VegaChart definitions load with their references resolved offline against the document and the embedded shared types. Bundled schema generation is tracked in [#67](https://github.com/bolorundurowb/a2ui-dotnet/issues/67). The conformance report records exclusions for legacy routing, abstract per-surface catalog selection, and fixtures that disagree with the pinned schema. For example, the function override fixture in `multi_catalog.yaml` references an undefined local `#/$defs/DynamicString`. The harness supplies the pinned `common_types.json#/$defs/DynamicString` reference and checks the resolved value through `NodeResolver`.

The Core smoke check covers a Unicode component and function argument schema, rejection of an invalid argument name, a renderer function that requires user activation, and explicit component routing on a mixed catalog surface.

## Core state behavior

`SurfaceSession` defaults to progressive topology validation for streamed updates. It accepts temporarily missing roots, forward references and orphans. Use `GraphValidationOptions.Strict` to require a complete reachable graph after each nonempty component batch. Both modes enforce composition, cycles and a 50-edge graph nesting limit before committing. Core also checks JSON container nesting in component values and data models, limits nested component function calls to five, and rejects renderer bindings or actions that call agent-only functions. A component with an explicit `@index` call may arrive before its parent in a progressive update. When the component is reachable from the root, every occurrence of it must be inside a collection template. A rejected operation preserves the previous snapshot and emits no change event.

Hosts serialize calls into each session. Local `WriteData` calls and remote `updateDataModel` messages commit in call order, with the last successful write taking effect. Reads use immutable snapshots. A missing pointer returns false, and a stored JSON null returns true with a null value. A null write deletes an object member, leaves a null slot in an array, or resets the root to an empty object. Change events carry the committed snapshot and the write's pointer so consumers can refresh bindings that overlap it. Reentrant writes are rejected.

The Release correctness run passed 446 Core cases and 247 Agent cases from the pinned upstream suites on .NET 8.0.34 for Windows x64. It recorded 419 Core and 783 Agent exclusions and no failures. In a separate scaling run on the same runtime, both progressive graph scaling workloads exceeded the upstream 8× growth limit. The Basic Catalog workload took 20.2 ms at 200 updates and 295.5 ms at 800, a 14.60× increase. The composition workload took 467.8 ms at 1,000 updates and 15,507.2 ms at 4,000, a 33.15× increase. The processor rebuilds and validates the candidate graph per component batch. [Performance tracking](https://github.com/bolorundurowb/a2ui-dotnet/issues/26)

The Direct JSON streaming parser reads the block again on each chunk and applies new messages through the same session. The three streaming growth cases exceeded the 8× limit at 16.80×, 15.84× and 11.87×. The streaming overhead case took 710.32× the compile time against a limit of 10×. The two compile growth cases passed at 4.39× and 3.79×. [Investigation](https://github.com/bolorundurowb/a2ui-dotnet/issues/56)

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
