param(
    [Parameter(Mandatory = $true)][string]$BaselineRoot,
    [string]$CurrentRoot,
    [string]$CheckpointRoot
)
$ErrorActionPreference = 'Stop'
if (-not $CurrentRoot) { $CurrentRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path }
$project = Join-Path $PSScriptRoot 'FluidParity.csproj'
function Run-Edition([string]$Name, [string]$Root, [bool]$IsBaseline) {
    $outDir = Join-Path $PSScriptRoot "artifacts/$Name"
    $csv = Join-Path $PSScriptRoot "$Name.csv"
    $baselineValue = $IsBaseline.ToString().ToLowerInvariant()
    & dotnet build $project "-p:SourceRoot=$Root" "-p:Baseline=$baselineValue" -o $outDir -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $Name" }
    & dotnet (Join-Path $outDir 'FluidParity.dll') $csv
    if ($LASTEXITCODE -ne 0) { throw "Simulation failed: $Name" }
    return $csv
}
function Compare-Edition([string]$BaselineCsv, [string]$CandidateCsv) {
    $baseline = Get-Content -LiteralPath $BaselineCsv
    $candidate = Get-Content -LiteralPath $CandidateCsv
    if ($baseline.Count -ne $candidate.Count) { throw 'Frame counts differ' }
    $different = 0
    for ($i = 1; $i -lt $baseline.Count; $i++) {
        if ($baseline[$i] -cne $candidate[$i]) {
            if ($different -lt 5) { Write-Host "Difference at $($baseline[$i].Split(',')[0..1] -join '/')" }
            $different++
        }
    }
    Write-Host "$($candidate.Count - 1) frames, $different different complete rows: $CandidateCsv"
    return $different
}
# Capture only returned paths, not dotnet's informational stdout.
$baselineCsv = Join-Path $PSScriptRoot 'baseline.csv'
$currentCsv = Join-Path $PSScriptRoot 'current.csv'
Run-Edition 'baseline' $BaselineRoot $true | Out-Host
Run-Edition 'current' $CurrentRoot $false | Out-Host
if ((Compare-Edition $baselineCsv $currentCsv) -ne 0) { exit 1 }
if ($CheckpointRoot) {
    Run-Edition 'checkpoint' $CheckpointRoot $false | Out-Host
    if ((Compare-Edition $baselineCsv (Join-Path $PSScriptRoot 'checkpoint.csv')) -eq 0) {
        throw 'Expected the historical faulty checkpoint to differ; regression detection was not demonstrated'
    }
}
Write-Host 'PASS: baseline/current complete numeric and geometry parity; standalone source isolation checks passed.'
