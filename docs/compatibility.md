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

## Deferred capabilities

| Capability | Decision |
| --- | --- |
| A2UI 0.9.1 and other legacy wire versions | Excluded from the stable profile. Reconsider 0.9.1 with a named consumer and captured payloads in the required directions. |
| Express streaming | Excluded. Reconsider when pinned upstream grammar, emission semantics and fixtures support a reproducible implementation. |
| Macros and optional catalog extensions | Excluded. Reconsider with a named consumer and pinned evidence for the relevant behavior. |
| Renderer framework adapters and additional transports | Excluded from the SDK packages. Evaluate with a named consumer. The SSE sample is a reference host integration. |
| Trimming and Native AOT | No compatibility claim. Require a clean published consumer to pass for each claimed deployment mode. Revisit Native AOT when a named consumer needs it. |

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
