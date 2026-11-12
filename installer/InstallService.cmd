@echo off
REM Registers IntranetSyncAgent as a Windows Service (admin required).
REM Prefer InstallUtil (ProjectInstaller); fall back to sc create.
setlocal
set "EXE=%~dp0IntranetSync.Service.exe"
set "SVC=IntranetSyncAgent"
set "DISP=Intranet SQL Cloud Sync Agent"
set "DESC=Incrementally syncs SQL Server views to cloud API and uploads PDFs to OSS."

if not exist "%EXE%" (
  echo ERROR: Missing "%EXE%"
  exit /b 1
)

net session >nul 2>&1
if errorlevel 1 (
  echo ERROR: Run this script as Administrator.
  exit /b 1
)

set "IU="
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe" set "IU=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe"
if not defined IU if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\InstallUtil.exe" set "IU=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\InstallUtil.exe"

sc query "%SVC%" >nul 2>&1
if not errorlevel 1 (
  echo Service "%SVC%" already registered.
  goto :start
)

if defined IU (
  echo Using InstallUtil: "%IU%"
  "%IU%" "%EXE%"
  if errorlevel 1 (
    echo InstallUtil failed; trying sc create...
    goto :sccreate
  )
  goto :start
)

:sccreate
echo Using sc create...
sc create "%SVC%" binPath= "\"%EXE%\"" start= auto DisplayName= "%DISP%"
if errorlevel 1 (
  echo ERROR: Failed to create service.
  exit /b 1
)
sc description "%SVC%" "%DESC%" >nul

:start
sc start "%SVC%"
echo Done. Admin UI default: http://127.0.0.1:8787/
endlocal
