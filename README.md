# Frost Client

Frost Client is a Windows desktop Minecraft launcher built with WPF and .NET 8.

## Requirements

- Windows 10 or later
- .NET 8 SDK (Windows)
- Java installed and available on `PATH` for running Minecraft and external Forge/Fabric installers
- A Microsoft account for Microsoft authentication; offline mode is also available

## Run from source

Open a terminal in the folder containing `FrostClient.csproj`, then run:

```powershell
dotnet restore
dotnet build -c Release
dotnet run
```

## Publish

To create a self-contained Windows x64 publish folder:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

The output is placed under `bin/Release/net8.0-windows/win-x64/publish/`. Distribute the entire publish folder, including the `mods` directory.

## Features

- Dark monochrome interface
- Offline player names and interactive Microsoft sign-in
- Installed Minecraft version discovery
- Vanilla version installation through CmlLib
- Forge and Fabric installer integration
- RAM setting and saved launcher preferences
- Portable `.jar` mod discovery and copy-to-game-folder behavior

## Notes and limitations

- Forge installer versions are explicitly configured for a small set of Minecraft releases. Unsupported versions show an error instead of constructing a likely-invalid download URL.
- Java must be installed separately. A self-contained .NET publish does not include Java or Minecraft.
- Forge/Fabric installation depends on the external installers and network availability.
- Only install mods from sources you trust, and use mods compatible with your selected Minecraft version and loader.
- The project should be built and tested on Windows with the .NET 8 SDK. This environment does not have the .NET SDK available, so a successful compile could not be verified here.
