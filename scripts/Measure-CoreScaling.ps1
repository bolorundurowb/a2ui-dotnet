[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Runtime,
    [Parameter(Mandatory)][string] $Assets,
    [string] $OutputDirectory = 'artifacts/core-scaling',
    [switch] $WaitForIdle
)

$ErrorActionPreference = 'Stop'
$project = 'tests/Conformance/bin/Release/net8.0/Conformance.dll'
if (!(Test-Path -LiteralPath $project)) { throw 'Build tests/Conformance in Release first.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$report = Join-Path $OutputDirectory 'scaling.json'
$log = Join-Path $OutputDirectory 'scaling.log'
$environment = Join-Path $OutputDirectory 'environment.json'
$counter = '\Processor(_Total)\% Processor Time'

function Get-ProcessorLoad {
    Get-Counter $counter -SampleInterval 1 -MaxSamples 5 |
        ForEach-Object { [math]::Round($_.CounterSamples[0].CookedValue, 1) }
}

function Test-Idle([double[]] $samples) {
    $samples.Count -eq 5 -and @($samples | Sort-Object)[2] -le 10
}

$before = @(Get-ProcessorLoad)
$attempt = 1
while ($WaitForIdle -and !(Test-Idle $before) -and $attempt -lt 12) {
    $before = @(Get-ProcessorLoad)
    $attempt++
}
if ($WaitForIdle -and !(Test-Idle $before)) {
    throw "Processor load did not settle after ${attempt} sample sets: $($before -join ', ') percent."
}

dotnet --fx-version $Runtime $project $Assets $report --scaling | Tee-Object -FilePath $log
$exitCode = $LASTEXITCODE
$after = @(Get-ProcessorLoad)
[ordered]@{
    runtime = $Runtime
    revision = (git rev-parse HEAD)
    os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    logicalProcessors = [Environment]::ProcessorCount
    idleSampleAttempts = $attempt
    cpuPercentBefore = $before
    cpuPercentAfter = $after
    idleBefore = (Test-Idle $before)
    idleAfter = (Test-Idle $after)
} | ConvertTo-Json | Set-Content -Encoding utf8 $environment
if ($exitCode -notin 0, 1 -or !(Test-Path -LiteralPath $report)) { throw "Scaling runner exited with $exitCode." }

$results = (Get-Content -LiteralPath $report -Raw | ConvertFrom-Json).results
$expected = @(
    'test_scaling_process_single_component_messages_relaxed_v10'
    'test_scaling_process_single_component_messages_composition_v10'
)
foreach ($id in $expected) {
    $case = @($results | Where-Object { $_.suite -eq 'core' -and $_.id -eq $id })
    if ($case.Count -ne 1 -or $case[0].status -ne 'pass') {
        throw "Expected one passing Core scaling case for $id."
    }
}
$unchecked = @($results | Where-Object { $_.suite -eq 'core' -and
    $_.id -eq 'test_scaling_process_single_component_messages_v10' })
if ($unchecked.Count -ne 1 -or $unchecked[0].status -ne 'excluded' -or !$unchecked[0].reason) {
    throw 'Expected the Core case without topology validation to have a recorded exclusion.'
}

Write-Host "Core scaling passed on .NET $Runtime. CPU readings are in $environment."
