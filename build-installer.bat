@echo off
REM ============================================================
REM  YFG YASTA v2 — Build MSI installer for client + server
REM
REM  Prerequisites (one-time setup):
REM    - .NET 10 SDK installed (https://dot.net)
REM    - Internet access (for WiX v5 NuGet package restore)
REM
REM  Usage:
REM    1. Clone the repo:  git clone https://github.com/razishimi1400-cpu/YFGdeskV2.git
REM    2. cd YFGdeskV2
REM    3. Double-click build-installer.bat
REM       (or run in cmd: build-installer.bat)
REM
REM  Output:
REM    installer\bin\Release\YFGdesk-Setup.msi   (client installer)
REM    installer\bin\Release\YFGdesk-Server-Setup.msi  (server installer)
REM
REM  The MSI installer is self-contained — it bundles the entire
REM  .NET 10 runtime + WPF + WebView2 bindings, so users do NOT need
REM  to install .NET separately. Just run the .msi and YFG YASTA is
REM  installed in Program Files with Start Menu shortcuts.
REM ============================================================

setlocal enabledelayedexpansion
set REPO_ROOT=%~dp0
pushd "%REPO_ROOT%"

echo.
echo ============================================================
echo  YFG YASTA v2 - Installer Build Script
echo ============================================================
echo.

REM ---- Step 1: Publish the client (single-file, self-contained) ----
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

REM Make sure the .ico file exists in Assets
if not exist client\Assets\yfgdesk-mark.ico (
  echo WARNING: client\Assets\yfgdesk-mark.ico is missing - icon will not be embedded.
)

REM ---- Step 2: Publish the server (single-file, self-contained) ----
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

REM ---- Step 3: Build the client MSI installer ----
echo.
echo [3/4] Building client MSI installer (WiX v5)...
echo ------------------------------------------------------------
REM WiX v5 ships via NuGet — `dotnet build` will restore it automatically.
dotnet build installer\YfgDesk.Installer.wixproj ^
  -c Release ^
  -p:EnableWindowsTargeting=true
if errorlevel 1 (
  echo ERROR: Client MSI build failed.
  echo Make sure the WiX v5 SDK NuGet package can be restored.
  goto :error
)

REM ---- Step 4: Build the server MSI installer ----
echo.
echo [4/4] Building server MSI installer (WiX v5)...
echo ------------------------------------------------------------
dotnet build installer\YfgDesk.ServerInstaller.wixproj ^
  -c Release ^
  -p:EnableWindowsTargeting=true
if errorlevel 1 (
  echo WARNING: Server MSI build failed - skipping. ^

)

REM ---- Done ----
echo.
echo ============================================================
echo  Build complete!
echo ============================================================
echo.
echo  Client MSI:  %REPO_ROOT%installer\bin\Release\YFGdesk-Setup.msi
echo  Server MSI:  %REPO_ROOT%installer\bin\Release\YFGdesk-Server-Setup.msi
echo.
echo  Portable exes (no install needed):
echo  Client:      %REPO_ROOT%publish\client-single\YfgDesk.Client.exe
echo  Server:      %REPO_ROOT%publish\server-single\YfgDesk.Server.exe
echo.
pause
exit /b 0

:error
echo.
echo Build failed. See errors above.
pause
exit /b 1
