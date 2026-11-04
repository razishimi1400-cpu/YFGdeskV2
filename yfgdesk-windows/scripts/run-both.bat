@echo off
REM ============================================================
REM  YFGDESK v2 — Start both server + client (demo / dev)
REM
REM  NOTE: For the demo, the client connects to localhost:7070.
REM  In production, the client uses the embedded 3-server list
REM  with automatic failover (no env var needed).
REM ============================================================

start "YFGDESK v2 Server" cmd /k ""%~dp0\run-server.bat""
timeout /t 3 /nobreak >nul
call "%~dp0\run-client.bat"
