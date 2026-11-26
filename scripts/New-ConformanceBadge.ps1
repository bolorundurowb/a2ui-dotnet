[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CorrectnessReport,
    [Parameter(Mandatory)][string]$ScalingReport,
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$correctness = Get-Content -Raw -LiteralPath $CorrectnessReport | ConvertFrom-Json
$scaling = Get-Content -Raw -LiteralPath $ScalingReport | ConvertFrom-Json
$total = $correctness.results.Count
if ($correctness.scaling -ne $false -or $scaling.scaling -ne $true -or
    !$correctness.revision -or $correctness.revision -cne $scaling.revision -or
    $total -eq 0 -or $total -ne $scaling.results.Count) {
    throw 'Expected correctness and scaling reports for the same upstream inventory.'
}

$passed = 0
$failed = 0
for ($index = 0; $index -lt $total; $index++) {
    $case = $correctness.results[$index]
    $timingCase = $scaling.results[$index]
    if ($case.suite -cne $timingCase.suite -or $case.file -cne $timingCase.file -or $case.id -cne $timingCase.id) {
        throw "Conformance inventories differ at row $index."
    }

    if ($case.status -eq 'fail' -or $timingCase.status -eq 'fail') {
        $failed++
    } elseif ($case.status -eq 'pass' -or $timingCase.status -eq 'pass') {
        $passed++
    }
}

$passRate = 100.0 * $passed / $total
$color = if ($passRate -lt 50) {
    '#a4262c'
} elseif ($passRate -lt 80) {
    '#8a5a00'
} elseif ($passRate -lt 90) {
    '#596b16'
} elseif ($passRate -lt 100) {
    '#2b6b4a'
} else {
    '#237438'
}
$message = "$passed / $total passing"
$labelWidth = 140
$messageWidth = 12 + 7 * $message.Length
$width = $labelWidth + $messageWidth
$messageCenter = $labelWidth + [int]($messageWidth / 2)
@"
<svg xmlns="http://www.w3.org/2000/svg" width="$width" height="20" role="img" aria-labelledby="title">
  <title id="title">Upstream conformance $message</title>
  <rect width="$width" height="20" rx="3" fill="$color"/>
  <path d="M3 0H$labelWidth V20H3a3 3 0 0 1-3-3V3a3 3 0 0 1 3-3Z" fill="#444"/>
  <g fill="#fff" font-family="Verdana, sans-serif" font-size="11" text-anchor="middle">
    <text x="70" y="14">upstream conformance</text>
    <text x="$messageCenter" y="14">$message</text>
  </g>
</svg>
"@ | Set-Content -LiteralPath $OutputPath -Encoding utf8

"$passed of $total upstream cases passed, $failed failed, and $($total - $passed - $failed) were excluded."
