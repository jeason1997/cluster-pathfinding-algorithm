param(
    [switch]$Check
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $root "src/CrowdNav"
$dstDir = Join-Path $root "Unity/CrowdNav.Unity/Assets/CrowdNav.Legacy"

if (-not (Test-Path $dstDir)) { throw "Missing Unity legacy folder: $dstDir" }

$srcFiles = Get-ChildItem $srcDir -Filter "*.cs" | Sort-Object Name
$drift = @()
foreach ($file in $srcFiles) {
    $dst = Join-Path $dstDir $file.Name
    if (-not (Test-Path $dst)) {
        $drift += "missing: $($file.Name)"
        continue
    }
    $a = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
    $b = (Get-FileHash $dst -Algorithm SHA256).Hash
    if ($a -ne $b) { $drift += "different: $($file.Name)" }
}
$srcNames = $srcFiles.Name
$extra = Get-ChildItem $dstDir -Filter "*.cs" | Where-Object { $srcNames -notcontains $_.Name }
foreach ($file in $extra) { $drift += "extra: $($file.Name)" }

if ($Check) {
    if ($drift.Count -gt 0) {
        Write-Host "Unity legacy copy is out of sync with src/CrowdNav:"
        $drift | ForEach-Object { Write-Host "  $_" }
        exit 1
    }
    Write-Host "Unity legacy copy is in sync with src/CrowdNav."
    exit 0
}

foreach ($file in $srcFiles) {
    Copy-Item $file.FullName (Join-Path $dstDir $file.Name) -Force
    Write-Host "copied $($file.Name)"
}
if ($extra.Count -gt 0) {
    Write-Host "Note: extra Unity-only legacy files are left in place:"
    $extra | ForEach-Object { Write-Host "  $($_.Name)" }
}
Write-Host "Synced src/CrowdNav -> Assets/CrowdNav.Legacy"
