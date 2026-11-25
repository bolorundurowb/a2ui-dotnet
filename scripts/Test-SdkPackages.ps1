[CmdletBinding()]
param([switch]$UpdatePublicApi)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$run = Join-Path $repository "artifacts/package-smoke/$([guid]::NewGuid())"
$feed = Join-Path $run 'feed'
$baseline = Join-Path $repository 'tests/PublicApi'
$packages = @('A2UI.Core', 'A2UI.Agent')
$revision = & git -C $repository rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Could not read the source revision.' }

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

function Read-ZipText {
    param([IO.Compression.ZipArchive]$Archive, [string]$Entry)

    $item = $Archive.GetEntry($Entry)
    if (!$item) { throw "Missing $Entry in the package archive." }
    $reader = [IO.StreamReader]::new($item.Open())
    try { $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Test-PackageArtifact {
    param([string]$Package, [string]$Version)

    $regular = [IO.Compression.ZipFile]::OpenRead((Join-Path $feed "$Package.$Version.nupkg"))
    try {
        $spec = [xml](Read-ZipText $regular "$Package.nuspec")
        $metadata = $spec.package.metadata
        $revision = & git -C $repository rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Could not read the source revision.' }
        if ($metadata.id -cne $Package -or $metadata.version -cne $Version -or
            $metadata.license.type -cne 'expression' -or $metadata.license.InnerText -cne 'MIT' -or
            $metadata.repository.type -cne 'git' -or $metadata.repository.commit -cne $revision -or
            $metadata.repository.url -cne 'https://github.com/bolorundurowb/a2ui-dotnet') {
            throw "$Package has unexpected NuGet metadata."
        }
        $dependencies = @($metadata.dependencies.group.dependency | Where-Object { $_ })
        $expectedCount = [int]($Package -eq 'A2UI.Agent')
        if ($metadata.dependencies.group.targetFramework -cne 'net8.0' -or
            $dependencies.Count -ne $expectedCount -or
            ($expectedCount -and ($dependencies[0].id -cne 'A2UI.Core' -or
                $dependencies[0].version -cne $Version))) {
            throw "$Package has an unexpected target framework or dependency list."
        }
        $projectLicense = Get-Content -Raw -LiteralPath (Join-Path $repository 'LICENSE')
        if ((Read-ZipText $regular 'LICENSE') -cne $projectLicense) {
            throw "$Package has an unexpected project licence."
        }
        foreach ($file in 'LICENSE','NOTICE.md') {
            $original = Get-Content -Raw -LiteralPath (Join-Path $repository "src/$Package/Upstream/$file")
            if ((Read-ZipText $regular "licenses/Upstream/$file") -cne $original) {
                throw "$Package has an unexpected upstream $file."
            }
        }
        $docs = [xml](Read-ZipText $regular "lib/net8.0/$Package.xml")
        if ($docs.doc.assembly.name -cne $Package -or !$docs.doc.members.member) {
            throw "$Package has no XML API documentation."
        }
    }
    finally { $regular.Dispose() }

    $symbols = [IO.Compression.ZipFile]::OpenRead((Join-Path $feed "$Package.$Version.snupkg"))
    try {
        $symbol = $symbols.GetEntry("lib/net8.0/$Package.pdb")
        if (!$symbol) { throw "Missing $Package portable symbols." }

        # Portable PDB readers require a seekable stream.
        $stream = $symbol.Open()
        $buffer = [IO.MemoryStream]::new()
        try {
            $stream.CopyTo($buffer)
            $buffer.Position = 0
            $provider = [Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($buffer)
            try {
                $reader = $provider.GetMetadataReader()
                $sourceLinkKind = [guid]'cc110556-a091-4d38-9fec-25ab9a351a6a'
                $sourceLink = $reader.CustomDebugInformation | ForEach-Object {
                    $entry = $reader.GetCustomDebugInformation($_)
                    if ($reader.GetGuid($entry.Kind) -eq $sourceLinkKind) {
                        [Text.Encoding]::UTF8.GetString($reader.GetBlobBytes($entry.Value)) | ConvertFrom-Json -AsHashtable
                    }
                }
                $expectedLink = "https://raw.githubusercontent.com/bolorundurowb/a2ui-dotnet/$revision/*"
                if (!$sourceLink -or $expectedLink -cnotin $sourceLink.documents.Values) {
                    throw "$Package portable symbols have no Source Link mapping for $revision."
                }
            }
            finally { $provider.Dispose() }
        }
        finally {
            $buffer.Dispose()
            $stream.Dispose()
        }
    }
    finally { $symbols.Dispose() }
}

$sdkProperties = [xml](Get-Content -Raw -LiteralPath (Join-Path $repository 'src/Directory.Build.props'))
$version = $sdkProperties.Project.PropertyGroup.Version
foreach ($package in $packages) {
    $project = Join-Path $repository "src/$package/$package.csproj"
    Invoke-DotNet pack $project -c Release -o $feed '-p:ContinuousIntegrationBuild=true'
    Test-PackageArtifact $package $version
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
        $assets = Get-Content -Raw -LiteralPath (Join-Path $consumer 'obj/project.assets.json') | ConvertFrom-Json -AsHashtable
        $expected = @("A2UI.Core/$version")
        if ($package -eq 'A2UI.Agent') { $expected += "A2UI.Agent/$version" }
        $actual = $assets.libraries.Keys | Sort-Object
        if (Compare-Object $expected $actual) {
            throw "Unexpected packages in the $framework $package consumer: $($actual -join ', ')."
        }

        $published = Join-Path $consumer 'publish'
        Invoke-DotNet publish $project @properties --no-restore --no-self-contained -c Release -o $published
        $assembly = Join-Path $published 'PackageSmoke.dll'
        $arguments = @("$runtime", $package)
        if ($major -eq 11) { $arguments += (Join-Path $consumer "$package.txt") }
        Invoke-DotNet exec --roll-forward Disable $assembly @arguments
        if ($major -ne 11) { continue }

        $snapshot = Join-Path $baseline "$package.txt"
        $generated = $arguments[2]
        if ($UpdatePublicApi) {
            New-Item -ItemType Directory -Force -Path $baseline | Out-Null
            Copy-Item -LiteralPath $generated -Destination $snapshot
        }
        elseif (!(Test-Path -LiteralPath $snapshot) -or
            (Get-Content -Raw -LiteralPath $snapshot).Replace("`r`n", "`n") -cne (Get-Content -Raw -LiteralPath $generated)) {
            throw "Public API changed for $package. Review $generated and run this script with -UpdatePublicApi."
        }
    }
}
