# CMB Connector Manager v1.0

A Windows desktop application for building, deploying, and debugging CMB carrier connectors locally.

## Features

### Build Chain
Full build pipeline for the CMB dependency chain:
- **Common** → **Framework** → **Core**
- One-click build with step-by-step progress tracking
- NuGet package output for downstream consumption

### API Manager
Local CMB API lifecycle management:
- Start/stop/restart the ASP.NET Core API
- Auto-detects Kestrel listening URLs from stdout
- Orphan process cleanup on restart (ports 9104/9105)

### Deploy Connector
Build, package, and upload carrier connectors:
- Scan the CarrierConnector repo for all connectors
- Search/filter by name or region
- Build and publish with automatic version computation
- Upload ZIP packages to the running local API
- Configurable auth header and version override

### Debug Connector
Attach Visual Studio to debug connector code:
- Builds connector in **Debug** configuration with matching PDBs
- Restarts the API with `--Package:DebugPackagePath` / `--Package:DebugPackageName`
- Auto-opens the connector solution in Visual Studio
- Attaches the VS debugger to the actual server process via COM/ROT automation
- Use version `debug` in API requests to hit breakpoints

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10/11
- Visual Studio (for Debug Connector feature)

## Repository Layout

```
ConnectorManager/
├── ConnectorManager.sln
├── Directory.Build.props
├── Directory.Packages.props
└── ConnectorManager/
    ├── ConnectorManager.csproj
    ├── App.xaml / MainWindow.xaml
    ├── Models/          # Data models and settings
    ├── Services/        # Business logic and process management
    ├── ViewModels/      # MVVM ViewModels (CommunityToolkit.Mvvm)
    ├── Views/           # WPF XAML views
    └── Converters/      # Value converters and behaviors
```

## Quick Start

### Build from source
```powershell
dotnet build ConnectorManager\ConnectorManager.csproj -c Release
```

### Publish self-contained EXE
```powershell
dotnet publish ConnectorManager\ConnectorManager.csproj -c Release -r win-x64 --self-contained -o bin\publish
```

### Run
```powershell
.\bin\publish\ConnectorManager.exe
```

## Setup

1. Launch the app
2. Go to **Settings** → click **Auto-Detect** (or set the DevTools repo path manually)
3. Repo paths propagate to each tab automatically
4. Set the **Authorization** header (e.g. `Basic <base64>`)

## Tech Stack

- **.NET 10.0** / WPF (`net10.0-windows`)
- **CommunityToolkit.Mvvm 8.4.0** — MVVM with source generators
- **Newtonsoft.Json 13.0.3** — JSON serialization
- **COM Interop** — Visual Studio automation via Running Object Table (ROT)
