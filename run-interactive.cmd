@echo off
cd /d "%~dp0"
echo Starting RTS interactive crowd demo at http://127.0.0.1:8799/
echo Press Ctrl+C to stop.
dotnet run --project src\CrowdSim.Interactive\CrowdSim.Interactive.csproj -c Release --no-launch-profile
pause
