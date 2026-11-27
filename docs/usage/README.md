# Use the A2UI SDKs

Choose the package for your role. Use `A2UI.Core` to process A2UI messages and resolve state in a renderer or headless application. Use `A2UI.Agent` to negotiate catalogues, validate and stream agent output and handle returned messages. The Agent package brings in Core.

| Example | Package | Walkthrough |
| --- | --- | --- |
| Process a form and send an action | `A2UI.Core` | [Core quickstart](core.md) |
| Send a deterministic turn and consume model chunks | `A2UI.Agent` | [Agent quickstart](agent.md) |
| Call functions in both directions | `A2UI.Agent` | [Function RPC](rpc.md) |

## Use the local packages

Build a temporary package feed from a source checkout by running `./scripts/Test-SdkPackages.ps1` with the runtimes in the [contribution guide](../../CONTRIBUTING.md#build-and-verify). The feed is the `feed` directory under that run in `artifacts/package-smoke/`. The script restores and runs the C# examples above in separate clean consumers. You can also create your own console project and install either package from that feed.

```powershell
dotnet new console --framework net8.0
dotnet add package A2UI.Core --version 0.1.0-dev --source $feed
```

Set `$feed` to the absolute path to the directory described above. For the Agent and RPC examples, install `A2UI.Agent` instead of `A2UI.Core`. Use the version in [SDK build properties](../../src/Directory.Build.props) if it differs from the example. Copy one C# example into `Program.cs` and run `dotnet run`. The SDK package version is independent of the A2UI wire version.

## Host responsibilities

The SDKs run in process. The application owns the model provider, credentials, transport, authorisation, data retention, cancellation policy and external effects. Bind renderer sessions, mirrors, RPC clients and data-model snapshots to the same authenticated owner. Configure catalogue and function registration before accepting input. Treat values from the model and renderer as untrusted and validate them again at the business boundary. Keep raw messages and synchronised values out of production telemetry.

The [ASP.NET Core sample](../../samples/AspNetCoreSse/README.md) demonstrates one loopback SSE binding with an HTTP action return channel. The [interoperability replay](../../tests/Interop/README.md) runs the appointment form headlessly in both directions against the pinned Python packages.

## Compatibility and deployment

The SDKs target `net8.0`. Clean local-feed consumer checks cover .NET 8 through .NET 11. The [compatibility matrix](../compatibility.md#net-package-compatibility) records runtime versions and verification. Use the [release profile](../compatibility.md#required-stable-coverage) for the pinned A2UI version, catalogue and inference format coverage and case exclusions. The [upstream baseline](../../upstream/README.md) records the pinned schema, suite and peer sources.

For a trimmed or Native AOT app, publish and run a clean consumer for your runtime and RID. Keep catalogue and handler registration explicit. Use source-generated metadata when serialising application DTOs in trimmed code, and review analyser warnings before deployment. The [deployment smoke instructions](../../CONTRIBUTING.md#trimming-and-native-aot) reproduce the repository's checks. See the [measured scaling and compatibility notes](../compatibility.md#core-state-behaviour) before choosing batch sizes or using incremental streaming for large surfaces.
