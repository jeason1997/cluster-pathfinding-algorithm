param(
    [switch]$Restore,
    [string]$Project = "Unity/CrowdNav.Unity"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $root $Project
$packages = Join-Path $projectPath "Packages"
$manifest = Join-Path $packages "manifest.json"
$registry = Join-Path $packages "manifest.registry.json"

if ($Restore) {
    if (-not (Test-Path $registry)) { throw "No manifest.registry.json backup found." }
    Copy-Item $registry $manifest -Force
    Write-Host "Restored registry manifest: $manifest"
    exit 0
}

if (-not (Test-Path $registry)) {
    Copy-Item $manifest $registry -Force
    Write-Host "Backed up registry manifest -> $registry"
}

$cacheRoot = Join-Path $env:LOCALAPPDATA "Unity/cache/packages/packages.unity.com"
$burst = Get-ChildItem $cacheRoot -Directory -Filter "com.unity.burst@*" -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
if ($null -eq $burst) {
    throw "No com.unity.burst@* found in $cacheRoot. Open the project once with network, or install Burst via Package Manager."
}
$path = $burst.FullName.Replace('\', '/')
$text = [System.IO.File]::ReadAllText($manifest)
$updated = [regex]::Replace($text, '"com\.unity\.burst"\s*:\s*"[^"]*"', '"com.unity.burst": "file:' + $path + '"')
if ($updated -eq $text -and $text -notmatch [regex]::Escape($path)) {
    throw "Could not find com.unity.burst dependency in manifest.json"
}
[System.IO.File]::WriteAllText($manifest, $updated, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Burst -> $path"
Write-Host "Manifest patched: $manifest"
Write-Host "Run with -Restore to put the registry version back."