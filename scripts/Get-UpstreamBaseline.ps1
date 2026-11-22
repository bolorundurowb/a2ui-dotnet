[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repository 'upstream/a2ui-baseline.json'
$baseline = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$run = Join-Path $repository "artifacts/upstream/$($baseline.revision)/$([guid]::NewGuid())"
$source = Join-Path $run 'source'
$output = Join-Path $run 'assets'
New-Item -ItemType Directory -Path $source, $output -Force | Out-Null

function Invoke-Git {
    & git -C $source @args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($args[0]) failed with exit code $LASTEXITCODE."
    }
}

Invoke-Git init --quiet
Invoke-Git fetch --quiet --no-tags --depth 1 $baseline.repository $baseline.revision
$revision = Invoke-Git rev-parse FETCH_HEAD
if ($revision -cne $baseline.revision) {
    throw "Fetched $revision, expected $($baseline.revision)."
}

foreach ($asset in $baseline.assets) {
    $object = "${revision}:$($asset.path)"
    $checksum = Invoke-Git rev-parse --verify $object
    $type = Invoke-Git cat-file -t $object
    if ($checksum -cne $asset.checksum -or $type -cne $asset.type) {
        throw "Upstream object mismatch at $($asset.path)."
    }
}

$archive = Join-Path $run 'assets.tar'
$paths = @($baseline.assets.path)
Invoke-Git archive --format=tar --output=$archive $revision -- @paths
& tar -xf $archive -C $output
if ($LASTEXITCODE -ne 0) {
    throw "tar failed with exit code $LASTEXITCODE."
}

Write-Output $output
