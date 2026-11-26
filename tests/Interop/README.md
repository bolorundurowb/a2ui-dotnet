# Cross-language appointment replay

The A2UI 1.0 replay runs a scripted appointment form against the published Python peers in the repository's [upstream baseline](../../upstream/a2ui-baseline.json). It uses the Python Core and Agent APIs and the .NET Core and Agent NuGet packages. No model, service credentials or renderer UI are required.

From the repository root, with .NET 8–11 and Python 3.12 installed, run:

```powershell
$baseline = Get-Content upstream/a2ui-baseline.json -Raw | ConvertFrom-Json
$requirements = @($baseline.peerSdks | ForEach-Object { "$($_.package)==$($_.version)" })
python -m pip install @requirements
./scripts/Test-SdkPackages.ps1 -InteropPython python
```

The package job in `build.yml` runs the same commands in CI. The replay publishes a clean .NET 8 consumer that restores from the package smoke test's temporary NuGet feed. The test writes `python.json` and `dotnet.json` into that run's `interop` directory under `artifacts/package-smoke`.

Each run generates a fresh surface ID. It compiles the fixture with both agents, applies the peer's wire messages in each renderer, edits the name field, routes the peer renderer's action, and checks data-model sync. It also checks that the validation rule reacts to the edit and that the template follows the changed slot order. Each SDK receives the peer's successful and failed RPC replies and settles the matching calls in both directions. The final turn renders a confirmation and deletes the surface. Comparison excludes action timestamps and compares RPC failure codes and correlation IDs independently of the implementation-specific error text.
