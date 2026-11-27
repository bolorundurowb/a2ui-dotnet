# Contributing to A2UI for .NET

Start with the [implementation plan](A2UI-DOTNET-PLAN.md) and [stable release profile](docs/compatibility.md). For a proposed design change, name the decision it affects, describe the consumer that needs it, and include any upstream evidence or small experiment that can settle it.

## Reporting interoperability problems

Include the A2UI protocol version, catalog ID, SDK revisions or releases, and any involved renderer or transport. Share a minimal sequence of messages with synthetic data and explain the expected and observed outcomes. Include the target framework and OS for .NET runtime or publish issues.

## Submitting changes

Keep each pull request focused. Describe the behavior it changes and the checks you ran. For protocol work, cite the corresponding upstream schema, specification text, or conformance fixture. If you import upstream material, include its source revision and preserve the applicable licenses and notices.

Use `master` as the default PR base. Agent branch names, commit subjects and PR conventions are documented in [AGENTS.md](AGENTS.md#git-conventions-for-agents).

New SDK library dependencies must be MIT-licensed, including their transitive library dependencies. For a performance change, include reproducible measurements and the runtime used.

## Build and verify

Use a .NET 11 SDK and the .NET 8 through 11 targeting packs and runtimes for the full smoke matrix. Run from the repository root in PowerShell.

```powershell
dotnet build A2UI.slnx -c Release
./scripts/Test-SdkPackages.ps1
./scripts/Get-UpstreamBaseline.ps1
```

The smoke script packs both SDKs in Release into a fresh local feed. It checks the NuGet metadata, licences and upstream notices, XML documentation, portable symbols, and Source Link mapping to the Git commit. It restores each package in a clean consumer for every runtime, checks the restored dependency graph, and publishes and runs each framework-dependent app. Agent consumers reference Agent alone and restore the matching Core package. The outputs are under `artifacts/package-smoke/`. The SDK projects use a fixed NuGet archive timestamp so clean rebuilds from the same commit and toolchain produce identical packages.

The package job also compiles and runs the C# examples extracted directly from the [SDK usage guide](docs/usage/README.md). Each example restores only from the temporary package feed. Keep the three examples self-contained when updating the guide.

To replay the cross-language appointment flow against clean SDK packages,
install the pinned Python peers and run `./scripts/Test-SdkPackages.ps1 -InteropPython python`.
See the [interoperability guide](tests/Interop/README.md) for setup and transcript details.

The shared `Version` in `src/Directory.Build.props` sets the NuGet package version for both SDKs using SemVer. Supported A2UI wire versions are recorded separately in the [compatibility matrix](docs/compatibility.md). Package name ownership, publishing credentials and checks against NuGet.org releases are part of the preview release.

When intentionally changing the SDK API, regenerate the signature snapshots from the package consumer and review their diff alongside the source changes.

```powershell
./scripts/Test-SdkPackages.ps1 -UpdatePublicApi
```

The snapshots in `tests/PublicApi/` record public and protected signatures from the packaged assemblies on .NET 11. The regular smoke run compares the generated signatures with the checked-in files. The [upstream guide](upstream/README.md) covers fixture provenance and refreshes.

## ASP.NET Core SSE sample

Run the backpressure smoke check from the repository root, then launch the
sample on loopback. Run the HTTP smoke script from a second terminal while the
server is running.

```powershell
dotnet run --project tests/AspNetCoreSseSmoke -c Release
dotnet run --project samples/AspNetCoreSse -c Release -- --urls http://127.0.0.1:5080
./samples/AspNetCoreSse/Test-Sample.ps1
```

See the [SSE sample guide](samples/AspNetCoreSse/README.md) for the framing,
metadata and reconnect contract.

## Pinned conformance and schema comparison

Run the correctness and scaling inventory separately. The JSON reports include
the upstream revision, case IDs, protocol versions when supplied by the suite,
and a status and reason for each case. The scaling run times the two pinned Core
progressive processor workloads through the UTF-8 codec and session. Run it
on an otherwise idle machine. Cases with unavailable SDK behavior have explicit exclusions.

```powershell
$assets = ./scripts/Get-UpstreamBaseline.ps1
dotnet run --project tests/Conformance -c Release -- $assets artifacts/conformance.json
dotnet run --project tests/Conformance -c Release -- $assets artifacts/conformance-scaling.json --scaling
dotnet run --project tests/SchemaComparison -c Release -- $assets
```

When refreshing the pinned assets, follow the [upstream refresh procedure](upstream/README.md#fetch-and-refresh), rerun both inventories and update the verification results and exclusions in [compatibility.md](docs/compatibility.md).

See the [schema profile and comparison](docs/schema-validation.md) for the
measured workload and current limits.

The conformance workflow uploads both case reports and a refreshed badge.
The badge counts each upstream case once and includes excluded cases in the
total. The badge colour reflects the percentage passing, with bands starting
at 50%, 80%, 90% and 100%. When a change affects conformance counts, include the refreshed SVG
in the same PR. To regenerate it locally from the reports above, run:

```powershell
./scripts/New-ConformanceBadge.ps1 artifacts/conformance.json artifacts/conformance-scaling.json docs/conformance-badge.svg
```

## Trimming and Native AOT

Both SDKs run trim and AOT analysis. Prefer source-generated `System.Text.Json` metadata and explicit registration of catalog and host types. If reflection needs to access members of a caller-supplied type, use `DynamicallyAccessedMembers` with the smallest required member set. Use `RequiresUnreferencedCode` or `RequiresDynamicCode` at an API boundary when an operation cannot satisfy the corresponding deployment mode. Preserve annotations when forwarding types and investigate warnings before using a suppression. [Microsoft trimming guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/prepare-libraries-for-trimming)

The deployment smoke project roots both SDK assemblies so publish analysis covers their code as it lands. Run both modes for your host RID. This Windows x64 example requires the Visual Studio Desktop development with C++ workload for Native AOT.

```powershell
dotnet publish tests/DeploymentSmoke -c Release -r win-x64 --self-contained -p:PublishTrimmed=true -o artifacts/deployment-smoke/trimmed
./artifacts/deployment-smoke/trimmed/DeploymentSmoke.exe trimmed
dotnet publish tests/DeploymentSmoke -c Release -r win-x64 -p:PublishAot=true -o artifacts/deployment-smoke/aot
./artifacts/deployment-smoke/aot/DeploymentSmoke.exe aot
```
