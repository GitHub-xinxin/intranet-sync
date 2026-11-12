@echo off
REM Stops and unregisters IntranetSyncAgent Windows Service (admin required).
setlocal
set "EXE=%~dp0IntranetSync.Service.exe"
set "SVC=IntranetSyncAgent"

net session >nul 2>&1
if errorlevel 1 (
  echo ERROR: Run this script as Administrator.
  exit /b 1
)

sc stop "%SVC%" >nul 2>&1

set "IU="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe" set "IU=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe"
if not defined IU if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\InstallUtil.exe" set "IU=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\InstallUtil.exe"

if defined IU if exist "%EXE%" (
  "%IU%" /u "%EXE%"
) else (
  sc delete "%SVC%"
)

echo Service uninstall attempted. Config under %%ProgramData%%\IntranetSyncAgent is kept.
endlocal
