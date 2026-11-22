[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$run = Join-Path $repository "artifacts/package-smoke/$([guid]::NewGuid())"
$feed = Join-Path $run 'feed'
$packages = @('A2UI.Core', 'A2UI.Agent')

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args[0]) failed with exit code $LASTEXITCODE."
    }
}

$runtimes = Invoke-DotNet --list-runtimes
$versions = @{}
foreach ($major in 8..11) {
    $versions[$major] = $runtimes | ForEach-Object {
        if ($_ -match "^Microsoft\.NETCore\.App ($major\.0\.\d+) ") {
            [version]$Matches[1]
        }
    } | Sort-Object -Descending | Select-Object -First 1
    if (!$versions[$major]) {
        throw "Install a stable .NET $major.0 runtime before running the package smoke test."
    }
}

$sdkProperties = [xml](Get-Content -Raw -LiteralPath (Join-Path $repository 'src/Directory.Build.props'))
$version = $sdkProperties.Project.PropertyGroup.Version
foreach ($package in $packages) {
    $project = Join-Path $repository "src/$package/$package.csproj"
    Invoke-DotNet pack $project -c Release -o $feed
}

foreach ($major in 8..11) {
    foreach ($package in $packages) {
        $framework = "net$major.0"
        $consumer = Join-Path $run "$framework/$package"
        New-Item -ItemType Directory -Force -Path $consumer | Out-Null
        Copy-Item -Path (Join-Path $repository 'tests/PackageSmoke/*.cs*') -Destination $consumer
        $project = Join-Path $consumer 'PackageSmoke.csproj'
        $runtime = $versions[$major]
        $properties = @(
            "-p:SmokeFramework=$framework"
            "-p:SmokePackage=$package"
            "-p:SmokeVersion=$version"
            "-p:RuntimeFrameworkVersion=$runtime"
            '-p:UseAppHost=false'
        )
        $cache = Join-Path $consumer 'packages'
        Invoke-DotNet restore $project @properties --source $feed --packages $cache --no-http-cache --force
        $published = Join-Path $consumer 'publish'
        Invoke-DotNet publish $project @properties --no-restore --no-self-contained -c Release -o $published
        $assembly = Join-Path $published 'PackageSmoke.dll'
        Invoke-DotNet exec --roll-forward Disable $assembly "$runtime" $package
    }
}
