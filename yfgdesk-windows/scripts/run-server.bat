@echo off
REM ============================================================
REM  YFGDESK v2 Server — Windows run script
REM  Self-contained (no .NET install needed).
REM  Uses %~dp0 so it works from any directory.
REM ============================================================

pushd "%~dp0\.."

REM --- Required: bearer token signing secret (>=32 random bytes base64)
if "%YFGDESK_TOKEN_SECRET%"=="" set YFGDESK_TOKEN_SECRET=dev_secret_replace_in_production_aaaaaaaaaaaaaaaaaaaaaaaa

REM --- Optional: TURN (only if you run coturn separately)
REM if "%YFGDESK_TURN_URLS%"=="" set YFGDESK_TURN_URLS=turn:your.host:3478?transport=udp,turn:your.host:3478?transport=tcp
REM if "%YFGDESK_TURN_SHARED_SECRET%"=="" set YFGDESK_TURN_SHARED_SECRET=REPLACE_WITH_64_HEX
REM if "%YFGDESK_TURN_REALM%"=="" set YFGDESK_TURN_REALM=your.host

REM --- SQLite database location (created next to the package root)
if "%YFGDESK_DB%"=="" set YFGDESK_DB=Data Source=yfgdesk.db

REM --- Delete old DB on each startup (dev only — for production use migrations)
if exist yfgdesk.db del /f /q yfgdesk.db 2>nul
if exist yfgdesk.db-shm del /f /q yfgdesk.db-shm 2>nul
if exist yfgdesk.db-wal del /f /q yfgdesk.db-wal 2>nul

REM --- Kestrel on port 7070 (the port the embedded client expects)
set ASPNETCORE_URLS=http://0.0.0.0:7070
set ASPNETCORE_ENVIRONMENT=Production

echo.
echo [YFGDESK v2] Starting signaling server on http://0.0.0.0:7070
echo [YFGDESK v2] Database: %YFGDESK_DB%
echo [YFGDESK v2] Working dir: %CD%
echo.

"server-win\YfgDesk.Server.exe"

set EXIT_CODE=%ERRORLEVEL%
popd

if %EXIT_CODE% NEQ 0 (
  echo.
  echo [YFGDESK v2] Server exited with code %EXIT_CODE%
  pause
)
