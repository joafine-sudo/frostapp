# Frost Client — Portable Build

## Add mods

1. Place trusted `.jar` mod files in the `mods` folder beside `FrostClient.csproj`.
2. Build or publish the launcher.
3. Distribute the complete publish folder, including `mods`.

## Build on Windows

Install the .NET 8 SDK, open PowerShell in the folder containing `FrostClient.csproj`, and run:

```powershell
dotnet restore
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true
```

A self-contained .NET publish includes the .NET runtime, but does not include Java, Minecraft, Forge, Fabric, or third-party mod dependencies. Confirm that you have permission to redistribute any included mods.
