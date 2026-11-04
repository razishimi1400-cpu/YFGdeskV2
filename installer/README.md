# YFG YASTA v2 — WiX v5 MSI Installer

Build MSI installers for **client** and **server** using WiX Toolset v5.

## Prerequisites (one-time)

1. **.NET 10 SDK** — install from https://dot.net
2. **Windows 10/11 x64** — WiX only runs on Windows
3. No need to install WiX separately — it's restored as a NuGet package (`WixToolset.Sdk/5.0.2`) automatically by `dotnet build`.

## Quick build (recommended)

```cmd
git clone https://github.com/razishimi1400-cpu/YFGdeskV2.git
cd YFGdeskV2
build-installer.bat
```

Output:

| File | Description |
|------|-------------|
| `installer\bin\Release\YFGdesk-Setup.msi` | Client MSI installer |
| `installer\bin\Release\YFGdesk-Server-Setup.msi` | Server MSI installer |
| `publish\client-single\YfgDesk.Client.exe` | Portable client (single-file, no install) |
| `publish\server-single\YfgDesk.Server.exe` | Portable server (single-file, no install) |

## Manual step-by-step

### Step 1: Publish the client (single-file, self-contained)

```cmd
dotnet publish client\YfgDesk.Client.csproj ^
  -c Release -f net10.0-windows -r win-x64 ^
  --self-contained true ^
  -p:EnableWindowsTargeting=true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:IncludeAllContentForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o .\publish\client-single
```

### Step 2: Publish the server (single-file, self-contained)

```cmd
dotnet publish server\YfgDesk.Server.csproj ^
  -c Release -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o .\publish\server-single
```

### Step 3: Build the client MSI

```cmd
dotnet build installer\YfgDesk.Installer.wixproj -c Release -p:EnableWindowsTargeting=true
```

Output: `installer\bin\Release\YFGdesk-Setup.msi`

### Step 4: Build the server MSI

```cmd
dotnet build installer\YfgDesk.ServerInstaller.wixproj -c Release -p:EnableWindowsTargeting=true
```

Output: `installer\bin\Release\YFGdesk-Server-Setup.msi`

## What the installers do

### Client MSI (`YFGdesk-Setup.msi`)

| Feature | Details |
|---------|---------|
| Install path | `C:\Program Files\YFGYasta\` |
| Start Menu shortcut | "YFG YASTA" in Start Menu |
| Desktop shortcut | Optional (user choice during install) |
| Add/Remove Programs | Full entry with uninstaller |
| Upgrade | Auto-kills running instance, then upgrades |
| UI language | Persian (fa-IR) |
| Self-contained | No .NET install required on target machine |
| Icon | YFG YASTA brand icon in taskbar, explorer, Alt-Tab |

### Server MSI (`YFGdesk-Server-Setup.msi`)

| Feature | Details |
|---------|---------|
| Install path | `C:\Program Files\YFGYastaServer\` |
| **Windows Service** | Installs as `YfgYastaServer` (auto-start on boot) |
| Service account | `LocalSystem` |
| Environment vars | `YFGDESK_DB`, `ASPNETCORE_URLS=http://0.0.0.0:7070`, `ASPNETCORE_ENVIRONMENT=Production` |
| Start Menu shortcut | "YFG YASTA Server (console)" for manual console mode |
| Uninstall | Auto-stops + removes the service |

## Files

```
installer/
├── YfgDesk.Installer.wixproj        ← Client MSI project (WiX v5 SDK)
├── YfgDesk.ServerInstaller.wixproj  ← Server MSI project (WiX v5 SDK)
├── Package.wxs                      ← Client MSI definition
├── ServerPackage.wxs                ← Server MSI definition (with Windows Service)
├── Strings.fa-IR.wxl                ← Persian localization strings
└── README.md                        ← This file
```

## Customizing

Edit `Package.wxs` (client) or `ServerPackage.wxs` (server):

- Change install folder name
- Add/remove shortcuts
- Add registry entries
- Add file type associations
- Change service configuration
- Add custom actions

## Troubleshooting

### `error WIX0150: Undefined preprocessor variable`

Run restore first:
```cmd
dotnet restore installer\YfgDesk.Installer.wixproj
```

### `error : Could not find file 'publish\client-single\...'`

You must run `build-installer.bat` (or at least the publish step) before building the MSI.

### `warning WIX0000: The WiX Toolset only supports Windows`

This is expected on Linux/macOS. WiX only runs on Windows. Use a Windows VM or CI runner.

## Building on Linux/macOS

Not supported — WiX v5 requires Windows. Use:
- A Windows VM (VirtualBox, VMware, Parallels)
- GitHub Actions Windows runner
- AppVeyor
