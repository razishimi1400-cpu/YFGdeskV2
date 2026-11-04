@echo off
REM ============================================================
REM  YFG YASTA — Build the MSI/EXE installer on Windows
REM
REM  Prerequisites:
REM    1. .NET 10 SDK installed
REM    2. Inno Setup 6 installed from https://jrsoftware.org/isdl.php
REM       (default install path: C:\Program Files (x86)\Inno Setup 6\)
REM
REM  Usage:
REM    1. Clone the repo: git clone https://github.com/razishimi1400-cpu/YFGdeskV2.git
REM    2. cd YFGdeskV2
REM    3. Double-click build-installer.bat  (or run in cmd)
REM    4. Output: publish\YFGdesk-Setup.exe
REM ============================================================

setlocal enabledelayedexpansion
set REPO_ROOT=%~dp0
pushd "%REPO_ROOT%"

echo.
echo [1/4] Publishing the client as a single-file self-contained exe...
echo ============================================================
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
  echo ERROR: dotnet publish failed.
  goto :error
)

echo.
echo [2/4] Assembling installer input folder...
echo ============================================================
if exist publish\installer rmdir /s /q publish\installer
mkdir publish\installer
copy /y publish\client-single\YfgDesk.Client.exe publish\installer\YFGdesk.exe
xcopy /e /i /y client\MediaAssets publish\installer\MediaAssets
xcopy /e /i /y client\Assets publish\installer\Assets

echo.
echo [3/4] Compiling the installer with Inno Setup...
echo ============================================================
set ISCC_PATH=
for %%P in (
  "C:\Program Files (x86)\Inno Setup 6\iscc.exe"
  "C:\Program Files\Inno Setup 6\iscc.exe"
  "%LOCALAPPDATA%\Programs\Inno Setup 6\iscc.exe"
) do (
  if exist %%P set ISCC_PATH=%%~P
)
if "%ISCC_PATH%"=="" (
  echo ERROR: Inno Setup iscc.exe not found.
  echo Please install Inno Setup 6 from https://jrsoftware.org/isdl.php
  goto :error
)

"%ISCC_PATH%" publish\YFGdesk.iss
if errorlevel 1 (
  echo ERROR: Inno Setup compilation failed.
  goto :error
)

echo.
echo [4/4] Done!
echo ============================================================
echo Installer created at:
echo   %REPO_ROOT%publish\YFGdesk-Setup.exe
echo.
echo Single-file portable exe:
echo   %REPO_ROOT%publish\installer\YFGdesk.exe
echo.
pause
exit /b 0

:error
echo.
echo Build failed. See errors above.
pause
exit /b 1
