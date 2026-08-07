param(
    [string]$ProfilePath = (Join-Path $PSScriptRoot '..\CombatSimulator\Resources\CharacterSurfaceProfiles.json')
)

$ErrorActionPreference = 'Stop'
$document = Get-Content -LiteralPath $ProfilePath -Raw | ConvertFrom-Json

if ($document.Version -ne 1) {
    throw "Unsupported character surface profile schema: $($document.Version)"
}

$expected = foreach ($race in 1..8) {
    foreach ($gender in 0..1) { "$race/$gender" }
}
$seen = @{}
foreach ($profile in $document.Profiles) {
    if ([string]::IsNullOrWhiteSpace($profile.Id) -or [string]::IsNullOrWhiteSpace($profile.Name)) {
        throw 'Every profile must have non-empty Id and Name values.'
    }
    $key = "$($profile.Race)/$($profile.Gender)"
    if ($seen.ContainsKey($key)) { throw "Duplicate race/gender profile: $key" }
    $seen[$key] = $profile.Id

    foreach ($role in $profile.RoleScales.PSObject.Properties) {
        foreach ($axis in 'Width', 'Depth', 'Length', 'TaperStart', 'TaperMiddle', 'TaperEnd') {
            $value = $role.Value.$axis
            if ($null -ne $value -and ($value -lt 0.25 -or $value -gt 2.0)) {
                throw "$($profile.Id).$($role.Name).$axis is outside the safe range: $value"
            }
        }
    }
}

$missing = @($expected | Where-Object { -not $seen.ContainsKey($_) })
if ($missing.Count -gt 0) { throw "Missing race/gender profiles: $($missing -join ', ')" }

Write-Output "Character surface profiles valid: $($document.Profiles.Count) profiles, schema $($document.Version)."
