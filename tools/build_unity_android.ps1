param(
    [string]$Unity = "C:\Program Files\Unity 6000.3.18f1\Editor\Unity.exe",
    [switch]$Bench,
    [string]$StageDir = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $root "Unity/CrowdNav.Unity"
$out = Join-Path $root "out/unity"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$method = if ($Bench) { "CrowdNav.Unity.EditorTools.UnityBuilder.BuildAndroidBench" } else { "CrowdNav.Unity.EditorTools.UnityBuilder.BuildAndroid" }
$name = if ($Bench) { "android_bench_build" } else { "android_build" }
$apkName = if ($Bench) { "CrowdNavBench.apk" } else { "CrowdNavDemo.apk" }

# Unity's Android tools refuse non-ASCII project paths. Stage a copy under an ASCII path.
$isAscii = $projectPath -notmatch '[^\x00-\x7F]'
if ($isAscii) {
    $buildProject = $projectPath
} else {
    if ([string]::IsNullOrWhiteSpace($StageDir)) {
        $StageDir = if (Test-Path "D:\") { "D:\CrowdNavUnityBuild" } else { Join-Path $env:TEMP "CrowdNavUnityBuild" }
    }
    if ($StageDir -match '[^\x00-\x7F]') { throw "StageDir must be ASCII: $StageDir" }
    New-Item -ItemType Directory -Force -Path $StageDir | Out-Null
    Write-Host "Non-ASCII project path; staging -> $StageDir"
    robocopy (Join-Path $projectPath "Assets") (Join-Path $StageDir "Assets") /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    robocopy (Join-Path $projectPath "ProjectSettings") (Join-Path $StageDir "ProjectSettings") /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $StageDir "Packages") | Out-Null
    Copy-Item (Join-Path $projectPath "Packages\manifest.json") (Join-Path $StageDir "Packages\manifest.json") -Force
    $buildProject = $StageDir
}

$log = Join-Path $out "$name.log"
if (Test-Path $log) { Remove-Item $log -Force }
Write-Host "Building $buildProject"
$proc = Start-Process -FilePath $Unity -ArgumentList @("-batchmode","-nographics","-projectPath",$buildProject,"-executeMethod",$method,"-logFile",$log) -PassThru
Write-Host "Unity pid=$($proc.Id) log=$log"
$proc.WaitForExit()
Write-Host "Exit=$($proc.ExitCode)"
$stageApk = Join-Path $buildProject "Builds/Android/$apkName"
if (Test-Path $stageApk) {
    $repoBuilds = Join-Path $projectPath "Builds/Android"
    New-Item -ItemType Directory -Force -Path $repoBuilds | Out-Null
    $destApk = Join-Path $repoBuilds $apkName
    if ([System.IO.Path]::GetFullPath($stageApk) -ne [System.IO.Path]::GetFullPath($destApk)) {
        Copy-Item $stageApk $destApk -Force
    }
    Copy-Item $stageApk (Join-Path $out $apkName) -Force
    Write-Host ("APK {0:N1} MB: {1}" -f ((Get-Item $stageApk).Length / 1MB), $destApk)
} else {
    Write-Host "APK not found: $stageApk"
}
Select-String -Path $log -Pattern "BUILD_RESULT|BUILD_FAIL|error CS|Build Failed|Invalid project path" | Select-Object -Last 20
exit $proc.ExitCode
