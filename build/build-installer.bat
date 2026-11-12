@echo off
REM Build Release then compile Inno Setup script to dist\IntranetSyncAgent-Setup.exe
REM Configure ISCC path via ISCC env var or default Inno Setup 5/6 locations.
setlocal EnableExtensions
cd /d "%~dp0.."

set "ROOT=%CD%"
echo === Intranet Sync Agent: Installer build ===

call "%~dp0build-release.bat"
if errorlevel 1 (
  echo ERROR: Release build failed; aborting installer.
  exit /b 1
)

REM --- Locate ISCC.exe (Inno Setup Compiler) ---
if not defined ISCC set "ISCC="
if defined ISCC if not exist "%ISCC%" (
  echo WARNING: ISCC=%ISCC% not found; searching defaults...
  set "ISCC="
)

if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles(x86)%\Inno Setup 5\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 5\ISCC.exe"
if not defined ISCC if exist "%ProgramFiles%\Inno Setup 5\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 5\ISCC.exe"
if not defined ISCC if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not defined ISCC if exist "C:\Program Files (x86)\Inno Setup 5\ISCC.exe" set "ISCC=C:\Program Files (x86)\Inno Setup 5\ISCC.exe"

where ISCC >nul 2>&1 && if not defined ISCC for /f "delims=" %%I in ('where ISCC') do if not defined ISCC set "ISCC=%%I"

if not defined ISCC (
  echo ERROR: ISCC.exe not found.
  echo Install Inno Setup 5 or 6, or set ISCC to the full path of ISCC.exe.
  echo Example: set ISCC=C:\Program Files ^(x86^)\Inno Setup 6\ISCC.exe
  exit /b 1
)
echo Using ISCC: %ISCC%

if not exist "%ROOT%\installer\IntranetSyncAgent.iss" (
  echo ERROR: Missing installer\IntranetSyncAgent.iss
  exit /b 1
)

if not exist "%ROOT%\dist" mkdir "%ROOT%\dist"

"%ISCC%" "%ROOT%\installer\IntranetSyncAgent.iss"
if errorlevel 1 (
  echo ERROR: Inno Setup compile failed
  exit /b 1
)

if not exist "%ROOT%\dist\IntranetSyncAgent-Setup.exe" (
  echo ERROR: Expected output missing: dist\IntranetSyncAgent-Setup.exe
  exit /b 1
)

echo.
echo Installer OK:
echo   %ROOT%\dist\IntranetSyncAgent-Setup.exe
echo.
echo Copy Setup.exe to the target Server 2012 host and run as Administrator.
echo Aliyun / API credentials are NOT collected by the installer — use Admin UI after install.
endlocal
exit /b 0
