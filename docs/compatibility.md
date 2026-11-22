# A2UI 1.0 stable release profile

This matrix defines the first stable release target. The current implementation verifies SDK scaffolding and package loading. Protocol coverage requires the applicable pinned suites and the two-way scripted interoperability replay. See the [upstream baseline](../upstream/README.md).

## First consumer scenario

Assume a .NET appointment-booking backend and an upstream Python Core headless test client. Use the pinned Basic Catalog for the first replay. Also exercise a small registered custom catalog and a mixed-catalog surface before claiming custom catalog coverage. The replay uses an in-process bridge and a scripted provider with synthetic data. The ASP.NET Core sample will demonstrate ordered SSE delivery and a POST return channel. Model clients, credentials, transport, durable state and business effects belong to the host.

The first turn negotiates capabilities, creates the form and sends separate component and data updates. The client edits a field, reorders a templated list and returns the submit action with resolved context and opted-in data. The next turn confirms the submission and deletes the surface. Replay both .NET Agent → upstream Core and upstream Agent → .NET Core. These are scenario assumptions pending a named integration and captured transcripts.

## Required stable coverage

| Area | Release target | Verification |
| --- | --- | --- |
| Core wire and state | A2UI 1.0 in both directions, including versioned metadata, ordered surface lifecycle, strict and progressive graph validation, JSON Pointer updates and reactive bindings, expressions and scopes. | Pending pinned Core cases and replay. |
| Core catalogs and interaction | Basic Catalog components and portable functions, offline registered custom and mixed catalogs, resolved accessibility attributes, actions, diagnostics and owner-scoped data sync. Host-bound functions use explicit registration. | Pending pinned Core cases and replay. |
| RPC | Agent → renderer calls and renderer → agent calls, each with replies in the opposite direction. Validate caller, activation and arguments. Cover correlation, errors, cancellation and reactive sharing. | Pending pinned Core and Agent cases and replay. |
| Agent runtime | Capability and catalog negotiation, fixed and negotiated catalogs, policy-gated inline catalogs, pruning, prompts, examples and skills. Validate deterministic and generated messages through Core. Route actions, diagnostics and RPC across turns. | Pending pinned Agent cases and replay. |
| Direct JSON | Batch compile, decompile and response parsing, plus incremental streaming with ordered text and UI, partial-result diagnostics, cancellation and size limits. | Pending pinned Agent cases and scaling checks. |
| Express | Batch compile, decompile, wrapping, response parsing and prompt generation with the Basic Catalog golden corpus. | Pending pinned Agent cases and scaling checks. |
| Express streaming | Incremental parsing and emission, ordered text and UI, and diagnostics for partial or malformed output. | Pending [#33](https://github.com/bolorundurowb/a2ui-dotnet/issues/33) and the pinned Express streaming cases. |
| Agent macros | Catalog-aware expansion with scoped IDs, nested macros and shared Core validation. | Pending [#34](https://github.com/bolorundurowb/a2ui-dotnet/issues/34) and the pinned v1.0 macro cases. |
| Trimming and Native AOT | Preserve support in the Core and Agent API design. Verify headless consumers for each claimed deployment target. | Windows x64 scaffold publish and execution passed for trimmed .NET 8 and Native AOT .NET 8 and 10. End-to-end package verification with protocol APIs is pending. |

## Optional integrations

| Capability | Planned scope |
| --- | --- |
| Optional catalog extensions | Verify declared extension catalogs and their host requirements, starting with MCP and VegaChart v1 as candidates. [#35](https://github.com/bolorundurowb/a2ui-dotnet/issues/35) |
| Renderer framework adapters | Ship separately from the SDKs. Select and verify the first adapter, with Blazor as a candidate. [#36](https://github.com/bolorundurowb/a2ui-dotnet/issues/36) |
| Additional transports | Ship independently of the SDKs. Select and verify the first binding beyond the ASP.NET Core SSE sample, with A2A and AG-UI as candidates. [#37](https://github.com/bolorundurowb/a2ui-dotnet/issues/37) |

A2UI 0.9.1 and other legacy wire versions remain outside the stable profile. Reconsider 0.9.1 with a named consumer and captured payloads in the required directions.

## .NET package compatibility

Both SDK packages target `net8.0` and have no third-party library dependencies. `A2UI.Agent` depends on `A2UI.Core`, and the package version `0.1.0-dev` is independent of the wire version. Public NuGet returned HTTP 404 for the candidate IDs `A2UI.Core` and `A2UI.Agent` on 22 November 2026. Confirm ownership and availability before publication.

Release build and local package smoke verification ran on Windows x64 with SDK 11.0.100. The checks load both packages in clean consumers from an isolated local feed with roll-forward disabled.

| Runtime | Scaffold smoke result | Microsoft servicing as of 22 November 2026 |
| --- | --- | --- |
| .NET 8 | Passed on 8.0.34 | Ended 10 November 2026 |
| .NET 9 | Passed on 9.0.23 | Ended 10 November 2026 |
| .NET 10 | Passed on 10.0.15 | Active LTS |
| .NET 11 | Passed on 11.0.0 | Active STS |

Microsoft tracks servicing independently of package compatibility. [Official .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
