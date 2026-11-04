@echo off
REM ============================================================
REM  YFGDESK v2 Client (WPF) — Windows run script
REM  Self-contained (no .NET 10 Desktop Runtime needed).
REM
REM  Server URLs are EMBEDDED in the binary (obfuscated). The client
REM  automatically fails over between three production servers:
REM    - 188.121.147.94:7070  (primary)
REM    - 37.114.216.224:7070  (fallback 1)
REM    - 94.182.31.149:7070   (fallback 2)
REM  No environment variable needed.
REM
REM  To override (e.g. for development against a local server):
REM    set YFGDESK_SERVER_URL=http://localhost:6215
REM    run-client.bat
REM ============================================================

pushd "%~dp0\.."

if not "%YFGDESK_SERVER_URL%"=="" (
  echo [YFGDESK v2] Using override server: %YFGDESK_SERVER_URL%
  set YFGDESK_DESKTOP_SERVERURL=%YFGDESK_SERVER_URL%
) else (
  echo [YFGDESK v2] Using embedded server list ^(3 servers, auto-failover^)
)

echo.
echo [YFGDESK v2] Starting client...
echo.

"client\YfgDesk.Client.exe"

set EXIT_CODE=%ERRORLEVEL%
popd

if %EXIT_CODE% NEQ 0 (
  echo.
  echo [YFGDESK v2] Client exited with code %EXIT_CODE%
  pause
)
