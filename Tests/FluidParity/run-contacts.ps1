param([string]$SourceRoot, [string]$HistoricalRoot, [switch]$HistoricalBaseline)
$ErrorActionPreference='Stop'
if(-not $SourceRoot){$SourceRoot=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path}
$project=Join-Path $PSScriptRoot 'FluidParity.csproj'
$current=Join-Path $PSScriptRoot 'artifacts/contact-current'
& dotnet build $project "-p:SourceRoot=$SourceRoot" '-p:Baseline=false' -o $current -v quiet
if($LASTEXITCODE -ne 0){throw 'Current production test build failed'}
& dotnet (Join-Path $current 'FluidParity.dll') --contacts
if($LASTEXITCODE -ne 0){throw 'Current production contact checks failed'}
if($HistoricalRoot){
    $historical=Join-Path $PSScriptRoot 'artifacts/contact-history'
    $baseline=$HistoricalBaseline.ToString().ToLowerInvariant()
    & dotnet build $project "-p:SourceRoot=$HistoricalRoot" "-p:Baseline=$baseline" -o $historical -v quiet
    if($LASTEXITCODE -ne 0){throw 'Historical production test build failed'}
    # A failed assertion is expected, but build errors or unrelated failures are not evidence.
    $ErrorActionPreference='Continue'
    try {
        $report=& dotnet (Join-Path $historical 'FluidParity.dll') --contacts 2>&1
        $code=$LASTEXITCODE
    } finally { $ErrorActionPreference='Stop' }
    $text=$report -join "`n"
    $report | Out-Host
    foreach($name in @('zero terrain and skin budgets','zero skin budget','unavailable native skin query','deferred ground sweep and conservation','partial skin transfer continues to ground in same sweep','terrain queries fairly visit the drop pool','convex rivulet material support')){
        if(-not $text.Contains("FAIL contact: ${name}:")){throw "Historical failure was not reproduced: $name"}
    }
    if($code -eq 0 -or -not $text.Contains('PASS contact: saturated body transfers remain permeable')){
        throw 'Historical negative control did not show the expected result'
    }
    Write-Host 'PASS: current eight contact checks and historical seven expected failures.'
}
