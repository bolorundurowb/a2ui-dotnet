# A2UI for .NET

[![Build](https://github.com/bolorundurowb/a2ui-dotnet/actions/workflows/build.yml/badge.svg?branch=master)](https://github.com/bolorundurowb/a2ui-dotnet/actions/workflows/build.yml)

A community project for building [A2UI](https://a2ui.org/) (Agent-to-User Interface) agents and processing A2UI messages in .NET. A2UI lets an agent describe interactive interfaces as structured messages. A compatible renderer displays those messages and sends user actions back to the agent.

## Project status

The packable SDK projects target `net8.0`. The [stable release profile](docs/compatibility.md) lists planned A2UI 1.0 coverage and verification status. See the [contribution guide](CONTRIBUTING.md#build-and-verify) for build and package smoke commands.

| Planned package | Responsibility |
| --- | --- |
| `A2UI.Core` | Protocol messages, catalogs, validation, surface state, and data binding |
| `A2UI.Agent` | Catalog negotiation, prompt generation, and compilation of streamed model output into validated A2UI messages |

The first milestone is a scripted form that verifies .NET Agent and Core interoperability with upstream SDKs in a headless test. The SDKs will use the .NET base class library where practical, with MIT-licensed third-party libraries only. The target is .NET 8 through 11 compatibility, with .NET 9 as the minimum if the .NET 8 feasibility check identifies a blocker.

## Contributing

Review the [implementation plan](A2UI-DOTNET-PLAN.md) and follow the [contribution guide](CONTRIBUTING.md). Include the upstream protocol version when discussing interoperability.

## License

Project code is available under the [MIT License](LICENSE). Any imported third-party files carry their applicable upstream licenses and notices.
