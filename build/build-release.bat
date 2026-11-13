@echo off
REM Restore NuGet packages and MSBuild Release for IntranetSyncAgent (.NET Framework 4.5.1).
REM Run on Windows with VS/Build Tools + .NET 4.5.1 targeting pack.
setlocal EnableExtensions
cd /d "%~dp0.."

set "ROOT=%CD%"
echo === Intranet Sync Agent: Release build ===
echo Root: %ROOT%

REM --- Locate nuget.exe ---
set "NUGET="
where nuget >nul 2>&1 && for /f "delims=" %%I in ('where nuget') do if not defined NUGET set "NUGET=%%I"
if not defined NUGET if exist "%ROOT%\nuget.exe" set "NUGET=%ROOT%\nuget.exe"
if not defined NUGET if exist "%ROOT%\tools\nuget.exe" set "NUGET=%ROOT%\tools\nuget.exe"
if not defined NUGET (
  echo nuget.exe not found on PATH or in repo. Downloading nuget.exe...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Invoke-WebRequest -Uri 'https://dist.nuget.org/win-x86-commandline/latest/nuget.exe' -OutFile '%ROOT%\nuget.exe'"
  if exist "%ROOT%\nuget.exe" set "NUGET=%ROOT%\nuget.exe"
)
if not defined NUGET (
  echo ERROR: Could not locate or download nuget.exe
  exit /b 1
)
echo Using NuGet: %NUGET%

"%NUGET%" restore "%ROOT%\IntranetSyncAgent.sln"
if errorlevel 1 (
  echo ERROR: nuget restore failed
  exit /b 1
)

REM --- Locate MSBuild ---
set "MSBUILD="
if defined MSBUILD_PATH if exist "%MSBUILD_PATH%" set "MSBUILD=%MSBUILD_PATH%"

if not defined MSBUILD if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" (
  for /f "usebackq delims=" %%I in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do if not defined MSBUILD set "MSBUILD=%%I"
)

if not defined MSBUILD if exist "%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles%\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
if not defined MSBUILD if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" set "MSBUILD=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if not defined MSBUILD if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe" set "MSBUILD=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"

where msbuild >nul 2>&1 && if not defined MSBUILD for /f "delims=" %%I in ('where msbuild') do if not defined MSBUILD set "MSBUILD=%%I"

if not defined MSBUILD (
  echo ERROR: MSBuild not found. Install VS/Build Tools or set MSBUILD_PATH.
  exit /b 1
)
echo Using MSBuild: %MSBUILD%

"%MSBUILD%" "%ROOT%\IntranetSyncAgent.sln" /t:Build /p:Configuration=Release /p:Platform="Any CPU" /m /v:minimal
if errorlevel 1 (
  echo ERROR: MSBuild Release failed
  exit /b 1
)

if not exist "%ROOT%\src\IntranetSync.Service\bin\Release\IntranetSync.Service.exe" (
  echo ERROR: Release output missing IntranetSync.Service.exe
  exit /b 1
)

echo.
echo Release build OK:
echo   %ROOT%\src\IntranetSync.Service\bin\Release\
endlocal
exit /b 0
