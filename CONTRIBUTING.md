# Contributing to A2UI for .NET

The project is in planning. Start with the [implementation plan](A2UI-DOTNET-PLAN.md). For a proposed design change, name the decision it affects, describe the consumer that needs it, and include any upstream evidence or small experiment that can settle it.

## Reporting interoperability problems

Include the A2UI protocol version, catalog ID, SDK revisions or releases, and any involved renderer or transport. Share a minimal sequence of messages with synthetic data and explain the expected and observed outcomes. Include the target framework and OS for .NET runtime or publish issues.

## Submitting changes

Keep each pull request focused. Describe the behavior it changes and the checks you ran. For protocol work, cite the corresponding upstream schema, specification text, or conformance fixture. If you import upstream material, include its source revision and preserve the applicable licenses and notices.

Use `master` as the default PR base. Agent branch names, commit subjects and PR conventions are documented in [AGENTS.md](AGENTS.md#git-conventions-for-agents).

New SDK library dependencies must be MIT-licensed, including their transitive library dependencies. For a performance change, include reproducible measurements and the runtime used.

Build, test and publish commands will be documented here as the SDK projects land.
