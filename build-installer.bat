@echo off
REM ============================================================
REM  YFG YASTA v2 — Build MSI installers for client + server
REM
REM  Produces:
REM    installer\bin\Release\YFGdesk-Setup.msi         (client MSI)
REM    installer\bin\Release\YFGdesk-Server-Setup.msi  (server MSI)
REM    publish\client-single\YfgDesk.Client.exe       (portable client)
REM    publish\server-single\YfgDesk.Server.exe       (portable server)
REM
REM  Prerequisites:
REM    - .NET 10 SDK installed (https://dot.net)
REM    - Internet access (for WiX v5 NuGet package restore)
REM    - Windows 10/11 x64 (WiX only runs on Windows)
REM
REM  Usage:
REM    1. git clone https://github.com/razishimi1400-cpu/YFGdeskV2.git
REM    2. cd YFGdeskV2
REM    3. Double-click build-installer.bat
REM ============================================================

setlocal enabledelayedexpansion
set REPO_ROOT=%~dp0
pushd "%REPO_ROOT%"

echo.
echo ============================================================
echo  YFG YASTA v2 - MSI Installer Build Script
echo  Repo: %REPO_ROOT%
echo  Date: %DATE% %TIME%
echo ============================================================
echo.

REM ---- Step 1/4: Publish client (single-file, self-contained) ----
echo [1/4] Publishing client as single-file self-contained exe...
echo ------------------------------------------------------------
dotnet publish client\YfgDesk.Client.csproj ^
  -c Release -f net10.0-windows -r win-x64 ^
  --self-contained true ^
  -p:EnableWindowsTargeting=true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:IncludeAllContentForSelfExtract=true ^
  -p:DebugType=none ^
  -p:EnableCompressionInSingleFile=true ^
  -o .\publish\client-single
if errorlevel 1 (
  echo ERROR: Client publish failed.
  goto :error
)
echo OK: Client published to .\publish\client-single\

REM ---- Step 2/4: Publish server (single-file, self-contained) ----
echo.
echo [2/4] Publishing server as single-file self-contained exe...
echo ------------------------------------------------------------
dotnet publish server\YfgDesk.Server.csproj ^
  -c Release -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:DebugType=none ^
  -p:EnableCompressionInSingleFile=true ^
  -o .\publish\server-single
if errorlevel 1 (
  echo ERROR: Server publish failed.
  goto :error
)
echo OK: Server published to .\publish\server-single\

REM ---- Step 3/4: Build client MSI installer ----
echo.
echo [3/4] Building client MSI installer (WiX v5)...
echo ------------------------------------------------------------
dotnet build installer\YfgDesk.Installer.wixproj ^
  -c Release ^
  -p:EnableWindowsTargeting=true
if errorlevel 1 (
  echo ERROR: Client MSI build failed.
  echo Possible fixes:
  echo   1. Run: dotnet restore installer\YfgDesk.Installer.wixproj
  echo   2. Make sure .\publish\client-single\ exists (step 1 should have created it)
  echo   3. Make sure client\Assets\yfgdesk-mark.ico exists
  goto :error
)
echo OK: Client MSI built: installer\bin\Release\YFGdesk-Setup.msi

REM ---- Step 4/4: Build server MSI installer ----
echo.
echo [4/4] Building server MSI installer (WiX v5)...
echo ------------------------------------------------------------
dotnet build installer\YfgDesk.ServerInstaller.wixproj ^
  -c Release ^
  -p:EnableWindowsTargeting=true
if errorlevel 1 (
  echo WARNING: Server MSI build failed - skipping.
  echo The client MSI was still built successfully.
) else (
  echo OK: Server MSI built: installer\bin\Release\YFGdesk-Server-Setup.msi
)

REM ---- Done ----
echo.
echo ============================================================
echo  Build complete!
echo ============================================================
echo.
echo  MSI installers:
echo    Client:  %REPO_ROOT%installer\bin\Release\YFGdesk-Setup.msi
echo    Server:  %REPO_ROOT%installer\bin\Release\YFGdesk-Server-Setup.msi
echo.
echo  Portable exes (no install needed):
echo    Client:  %REPO_ROOT%publish\client-single\YfgDesk.Client.exe
echo    Server:  %REPO_ROOT%publish\server-single\YfgDesk.Server.exe
echo.
echo  To install: double-click the .msi file.
echo  To uninstall: Settings > Apps > YFG YASTA > Uninstall.
echo.
pause
exit /b 0

:error
echo.
echo ============================================================
echo  Build FAILED. See errors above.
echo ============================================================
pause
exit /b 1
