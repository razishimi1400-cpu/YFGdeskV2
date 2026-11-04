# YFG YASTA v2 — Installer

Build MSI installers for **client** and **server** using WiX Toolset v5.

## Prerequisites (one-time)

1. **.NET 10 SDK** — install from https://dot.net
2. **Windows 10/11 x64** — WiX only runs on Windows

## Build the client MSI installer

Open CMD in the repo root and run:

```cmd
build-installer.bat
```

Or step-by-step:

```cmd
:: 1. Publish the client as a single-file exe
dotnet publish client\YfgDesk.Client.csproj ^
  -c Release -f net10.0-windows -r win-x64 ^
  --self-contained true ^
  -p:EnableWindowsTargeting=true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:IncludeAllContentForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o .\publish\client-single

:: 2. Build the MSI installer (WiX v5 is restored automatically from NuGet)
dotnet build installer\YfgDesk.Installer.wixproj -c Release -p:EnableWindowsTargeting=true
```

Output: `installer\bin\Release\YFGdesk-Setup.msi`

## Build the server MSI installer

```cmd
:: 1. Publish the server as a single-file exe
dotnet publish server\YfgDesk.Server.csproj ^
  -c Release -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o .\publish\server-single

:: 2. Build the MSI installer
dotnet build installer\YfgDesk.ServerInstaller.wixproj -c Release -p:EnableWindowsTargeting=true
```

Output: `installer\bin\Release\YFGdesk-Server-Setup.msi`

## What the installers do

### Client MSI (`YFGdesk-Setup.msi`)

- Installs YFG YASTA into `C:\Program Files\YFGSYSTEMS\YFGYasta\`
- Creates Start Menu shortcut "YFG YASTA"
- Creates optional Desktop shortcut (user choice)
- Adds entry to "Add or remove programs" with uninstaller
- Auto-kills any running instance before upgrade
- Bilingual installer UI (Persian + English)
- Self-contained — no .NET install required on target machine

### Server MSI (`YFGdesk-Server-Setup.msi`)

- Installs YFG YASTA Server into `C:\Program Files\YFGSYSTEMS\YFGYastaServer\`
- **Installs as a Windows Service** (auto-start on boot, runs as LocalSystem)
- Service name: `YfgYastaServer`
- Sets environment variables:
  - `YFGDESK_DB` → `C:\Program Files\YFGSYSTEMS\YFGYastaServer\yfgdesk.db`
  - `YFGDESK_TOKEN_SECRET` → `REPLACE_AT_INSTALL` (you must change this)
  - `ASPNETCORE_URLS` → `http://0.0.0.0:7070`
  - `ASPNETCORE_ENVIRONMENT` → `Production`
- Start Menu shortcut "YFG YASTA Server (console)" for manual console mode
- Auto-stops service on uninstall

## Customizing the installer

Edit `installer/Package.wxs` (client) or `installer/ServerPackage.wxs` (server):

- Change install folder name (`Name="YFGYasta"`)
- Add/remove shortcuts
- Add registry entries
- Add file type associations
- Change service configuration

## Localization

Persian strings are in `installer/Strings.fa-IR.wxl`. To add another language:

1. Copy `Strings.fa-IR.wxl` → `Strings.en-US.wxl`
2. Translate the `<String>` values
3. Add `<UIRef Id="WixUI_en-US" />` to `Package.wxs`

## Building on Linux/macOS

WiX v5 only runs on Windows. To build the MSI from Linux, use a Windows VM
or Windows CI runner (GitHub Actions, AppVeyor).

## Files

```
installer/
├── YfgDesk.Installer.wixproj       ← Client MSI project
├── YfgDesk.ServerInstaller.wixproj ← Server MSI project
├── Package.wxs                     ← Client MSI definition
├── ServerPackage.wxs               ← Server MSI definition
├── Strings.fa-IR.wxl               ← Persian localization
└── README.md                       ← This file
```
