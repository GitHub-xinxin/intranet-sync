@echo off
REM Opens the local Admin UI. Port defaults to 8787; reads Admin.Port from config when present.
setlocal
set "URL=http://127.0.0.1:8787/"
set "CFG=%ProgramData%\IntranetSyncAgent\config.json"
if exist "%CFG%" (
  for /f "usebackq delims=" %%U in (`powershell -NoProfile -ExecutionPolicy Bypass -Command "try { $p='%CFG%'; $c=Get-Content -Raw -LiteralPath $p | ConvertFrom-Json; 'http://127.0.0.1:{0}/' -f [int]$c.Admin.Port } catch { 'http://127.0.0.1:8787/' }"`) do set "URL=%%U"
)
start "" "%URL%"
endlocal
