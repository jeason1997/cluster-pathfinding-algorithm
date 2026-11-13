param(
    [string]$Apk = "Unity/CrowdNav.Unity/Builds/Android/CrowdNavDemo.apk"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$apkPath = Join-Path $root $Apk
if (-not (Test-Path $apkPath)) { throw "APK not found: $apkPath" }
$adb = Join-Path "C:\Program Files\Unity 6000.3.18f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools" "adb.exe"
if (-not (Test-Path $adb)) { $adb = "adb" }
& $adb devices
& $adb install -r $apkPath
& $adb shell am force-stop com.crowdnav.demo
& $adb shell monkey -p com.crowdnav.demo -c android.intent.category.LAUNCHER 1
Write-Host "Launched. Use: adb logcat -s Unity:I"