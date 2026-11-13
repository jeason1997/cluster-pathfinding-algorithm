param(
    [string]$Unity = "C:\Program Files\Unity 6000.3.18f1\Editor\Unity.exe",
    [int]$WaitSeconds = 240
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "out/unity"
New-Item -ItemType Directory -Force -Path $out | Out-Null
& (Join-Path $PSScriptRoot "build_unity_android.ps1") -Unity $Unity -Bench
if ($LASTEXITCODE -ne 0) { throw "Android bench build failed." }
$apk = Join-Path $root "Unity/CrowdNav.Unity/Builds/Android/CrowdNavBench.apk"
$adb = Join-Path "C:\Program Files\Unity 6000.3.18f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools" "adb.exe"
if (-not (Test-Path $adb)) { $adb = "adb" }
& $adb install -r $apk
& $adb logcat -c
& $adb shell am force-stop com.crowdnav.demo
& $adb shell monkey -p com.crowdnav.demo -c android.intent.category.LAUNCHER 1
Write-Host "Running device benchmark for $WaitSeconds s..."
Start-Sleep -Seconds $WaitSeconds
$log = Join-Path $out "device_logcat.txt"
& $adb logcat -d -s Unity:I | Tee-Object -FilePath $log | Select-String "CROWD_BENCH|CROWD GOAL|CROWD_BENCH_START|CROWD_BENCH_WROTE"
$remote = "/sdcard/Android/data/com.crowdnav.demo/files/crowd_bench.json"
& $adb pull $remote (Join-Path $out "crowd_bench.json")
Write-Host "Saved: $log"