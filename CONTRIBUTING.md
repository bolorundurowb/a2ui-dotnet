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
dotnet build A2UI.sln -c Release
./scripts/Test-SdkPackages.ps1
./scripts/Get-UpstreamBaseline.ps1
```

The smoke script packs both SDKs into a fresh local feed, restores each package in a clean consumer for every runtime, and publishes and runs each framework-dependent app. Agent consumers reference Agent alone and load Core through its package dependency. The outputs are under `artifacts/package-smoke/`. The [upstream guide](upstream/README.md) covers fixture provenance and refreshes.

## Trimming and Native AOT

Both SDKs run trim and AOT analysis. Prefer source-generated `System.Text.Json` metadata and explicit registration of catalog and host types. If reflection needs to access members of a caller-supplied type, use `DynamicallyAccessedMembers` with the smallest required member set. Use `RequiresUnreferencedCode` or `RequiresDynamicCode` at an API boundary when an operation cannot satisfy the corresponding deployment mode. Preserve annotations when forwarding types and investigate warnings before using a suppression. [Microsoft trimming guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/prepare-libraries-for-trimming)

The deployment smoke project roots both SDK assemblies so publish analysis covers their code as it lands. Run both modes for your host RID. This Windows x64 example requires the Visual Studio Desktop development with C++ workload for Native AOT.

```powershell
dotnet publish tests/DeploymentSmoke -c Release -r win-x64 --self-contained -p:PublishTrimmed=true -o artifacts/deployment-smoke/trimmed
./artifacts/deployment-smoke/trimmed/DeploymentSmoke.exe trimmed
dotnet publish tests/DeploymentSmoke -c Release -r win-x64 -p:PublishAot=true -o artifacts/deployment-smoke/aot
./artifacts/deployment-smoke/aot/DeploymentSmoke.exe aot
```
